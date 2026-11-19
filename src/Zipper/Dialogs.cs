using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows.Forms;

namespace Zipper
{
    class CompressForm : Form
    {
        static readonly string[] Formats = { "zip", "7z", "tar", "gz", "bz2", "xz", "wim" };
        static readonly (string Name, int Level)[] Levels = { ("저장 (압축 안 함)", 0), ("가장 빠름", 1), ("빠름", 3), ("보통", 5), ("높음", 7), ("최고", 9) };
        static readonly string[] Volumes = { "분할 안 함", "10 MB", "100 MB", "700 MB (CD)", "1 GB", "4092 MB (FAT32)", "4480 MB (DVD)" };

        public CompressOptions Options = new CompressOptions();
        readonly string[] files;
        readonly TextBox output = new TextBox { Width = 380 };
        readonly ComboBox format = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 160 };
        readonly ComboBox level = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 160 };
        readonly ComboBox volume = new ComboBox { Width = 160 };
        readonly TextBox password = new TextBox { Width = 220, UseSystemPasswordChar = true };
        readonly CheckBox encNames = new CheckBox { Text = "파일 이름도 암호화 (7z)", AutoSize = true, Checked = true };
        readonly CheckBox aes = new CheckBox { Text = "AES-256 암호화 (zip, 일부 구형 프로그램은 못 엶)", AutoSize = true };

        public CompressForm(string[] files, string fmt)
        {
            this.files = files;
            Ui.Dialog(this, "새로 압축");
            format.Items.AddRange(Formats);
            level.Items.AddRange(Levels.Select(l => l.Name).ToArray());
            volume.Items.AddRange(Volumes);
            level.SelectedIndex = 3; volume.SelectedIndex = 0;
            output.Text = Ops.DefaultOutput(files, fmt);
            format.SelectedItem = fmt;
            format.SelectedIndexChanged += (s, e) => { SyncExt(); SyncEnabled(); };
            password.TextChanged += (s, e) => SyncEnabled();

            var browse = Ui.Button("찾아보기...");
            browse.Margin = new Padding(6, 0, 0, 0);
            browse.Click += (s, e) =>
            {
                using (var d = new SaveFileDialog { FileName = Path.GetFileName(output.Text), InitialDirectory = Path.GetDirectoryName(output.Text), Filter = "압축 파일|*.*" })
                    if (d.ShowDialog(this) == DialogResult.OK) output.Text = d.FileName;
            };
            var show = new CheckBox { Text = "암호 표시", AutoSize = true };
            show.CheckedChanged += (s, e) => password.UseSystemPasswordChar = !show.Checked;

            var grid = Ui.Grid(3);
            void Row(string label, Control c, Control extra = null)
            {
                grid.Controls.Add(Ui.Label(label));
                grid.Controls.Add(c);
                grid.Controls.Add(extra ?? new Label { AutoSize = true });
            }
            var head = new Label
            {
                AutoSize = true, ForeColor = SystemColors.GrayText, Margin = new Padding(3, 0, 3, 8),
                Text = files.Length == 1 ? Path.GetFileName(files[0].TrimEnd('\\')) : $"{Path.GetFileName(files[0].TrimEnd('\\'))} 외 {files.Length - 1}개 항목",
            };
            grid.Controls.Add(head); grid.SetColumnSpan(head, 3);
            Row("압축 파일", output, browse);
            Row("형식", format);
            Row("압축 레벨", level);
            Row("분할 압축", volume, new Label { Text = "예: 50 MB, 2 GB", AutoSize = true, ForeColor = SystemColors.GrayText, Anchor = AnchorStyles.Left });
            Row("암호", password, show);
            grid.Controls.Add(new Label()); grid.Controls.Add(encNames); grid.SetColumnSpan(encNames, 2);
            grid.Controls.Add(new Label()); grid.Controls.Add(aes); grid.SetColumnSpan(aes, 2);
            var ok = Ui.Button("압축 시작");
            ok.Click += (s, e) => Accept();
            var buttons = Ui.Buttons(ok, Ui.Button("취소", DialogResult.Cancel));
            grid.Controls.Add(buttons); grid.SetColumnSpan(buttons, 3);
            Controls.Add(grid);
            AcceptButton = ok;
            SyncEnabled();
        }

        string Fmt => (string)format.SelectedItem;

        void SyncExt()
        {
            var p = output.Text;
            var ext = Path.GetExtension(p).TrimStart('.').ToLowerInvariant();
            if (Formats.Contains(ext)) output.Text = p.Substring(0, p.Length - ext.Length) + Fmt;
        }

        void SyncEnabled()
        {
            bool canPw = Fmt == "zip" || Fmt == "7z";
            password.Enabled = canPw;
            encNames.Enabled = canPw && Fmt == "7z" && password.Text.Length > 0;
            aes.Enabled = canPw && Fmt == "zip" && password.Text.Length > 0;
            level.Enabled = Fmt != "tar" && Fmt != "wim";
        }

        /// <summary>"100 MB" / "2gb" / "700m" -> 7z volume switch value; null for no split; "" if invalid.</summary>
        public static string ParseVolume(string text)
        {
            if (string.IsNullOrWhiteSpace(text) || text.StartsWith("분할")) return null;
            var m = Regex.Match(text.Trim(), @"^(\d+)\s*([kmg]?)b?", RegexOptions.IgnoreCase);
            if (!m.Success || m.Groups[1].Value.TrimStart('0').Length == 0) return "";
            return m.Groups[1].Value + (m.Groups[2].Value.Length > 0 ? m.Groups[2].Value.ToLowerInvariant() : "m");
        }

        void Accept()
        {
            var vol = ParseVolume(volume.Text);
            string err = null;
            if (output.Text.Trim().Length == 0 || !Path.IsPathRooted(output.Text)) err = "압축 파일 경로를 입력하세요.";
            else if (vol == "") err = "분할 크기를 확인하세요. 예: 100 MB, 2 GB";
            else if ((Fmt == "gz" || Fmt == "bz2" || Fmt == "xz") && (files.Length > 1 || Directory.Exists(files[0])))
                err = $"{Fmt} 형식은 파일 하나만 압축할 수 있습니다. 여러 파일은 zip, 7z, tar를 쓰세요.";
            if (err != null) { Ui.Error(this, err); return; }

            var outPath = output.Text.Trim();
            if (File.Exists(outPath))
            {
                if (MessageBox.Show(this, $"{Path.GetFileName(outPath)} 파일이 이미 있습니다. 덮어쓸까요?", "Zipper",
                        MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
                try { File.Delete(outPath); } catch (Exception ex) { Ui.Error(this, ex.Message); return; }
            }
            Options = new CompressOptions
            {
                Output = outPath, Format = Fmt, Level = Levels[level.SelectedIndex].Level, Volume = vol,
                Password = password.Enabled ? password.Text : null, EncryptNames = encNames.Checked, Aes = aes.Checked,
            };
            DialogResult = DialogResult.OK;
        }
    }

    class ExtractForm : Form
    {
        public static readonly string[] OverwriteNames = { "항상 덮어쓰기", "건너뛰기", "새 파일 이름 바꾸기" };
        readonly TextBox dest = new TextBox { Width = 380 };
        readonly ComboBox overwrite = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 180 };
        readonly CheckBox open = new CheckBox { Text = "풀고 나서 폴더 열기", AutoSize = true };
        public string Dest => dest.Text.Trim();
        public string OverwriteSwitch => new[] { "-aoa", "-aos", "-aou" }[overwrite.SelectedIndex];
        public bool OpenFolder => open.Checked;

        public ExtractForm(string archive, int selectedCount)
        {
            Ui.Dialog(this, "압축 풀기");
            dest.Text = Ops.FolderFor(archive);
            overwrite.Items.AddRange(OverwriteNames);
            overwrite.SelectedIndex = Shell.OverwriteMode;
            open.Checked = Shell.OpenFolderAfterExtract;

            var browse = Ui.Button("찾아보기...");
            browse.Margin = new Padding(6, 0, 0, 0);
            browse.Click += (s, e) =>
            {
                using (var d = new FolderBrowserDialog { SelectedPath = Directory.Exists(Dest) ? Dest : Path.GetDirectoryName(archive) })
                    if (d.ShowDialog(this) == DialogResult.OK) dest.Text = d.SelectedPath;
            };
            var here = new LinkLabel { Text = "압축 파일 위치", AutoSize = true, Margin = new Padding(3, 4, 12, 3) };
            here.LinkClicked += (s, e) => dest.Text = Path.GetDirectoryName(archive);
            var folder = new LinkLabel { Text = "새 폴더", AutoSize = true, Margin = new Padding(3, 4, 3, 3) };
            folder.LinkClicked += (s, e) => dest.Text = Ops.FolderFor(archive);
            var links = new FlowLayoutPanel { AutoSize = true, Margin = Padding.Empty };
            links.Controls.AddRange(new Control[] { here, folder });

            var grid = Ui.Grid(3);
            var head = new Label
            {
                AutoSize = true, ForeColor = SystemColors.GrayText, Margin = new Padding(3, 0, 3, 8),
                Text = Path.GetFileName(archive) + (selectedCount > 0 ? $"  ·  선택한 {selectedCount}개 항목" : "  ·  전체"),
            };
            grid.Controls.Add(head); grid.SetColumnSpan(head, 3);
            grid.Controls.Add(Ui.Label("풀 위치")); grid.Controls.Add(dest); grid.Controls.Add(browse);
            grid.Controls.Add(new Label()); grid.Controls.Add(links); grid.Controls.Add(new Label());
            grid.Controls.Add(Ui.Label("같은 파일이 있으면")); grid.Controls.Add(overwrite); grid.Controls.Add(new Label());
            grid.Controls.Add(new Label()); grid.Controls.Add(open); grid.Controls.Add(new Label());
            var ok = Ui.Button("풀기");
            ok.Click += (s, e) =>
            {
                if (Dest.Length == 0 || !Path.IsPathRooted(Dest)) { Ui.Error(this, "풀 위치를 입력하세요."); return; }
                DialogResult = DialogResult.OK;
            };
            var buttons = Ui.Buttons(ok, Ui.Button("취소", DialogResult.Cancel));
            grid.Controls.Add(buttons); grid.SetColumnSpan(buttons, 3);
            Controls.Add(grid);
            AcceptButton = ok;
        }
    }

    class SettingsForm : Form
    {
        public SettingsForm()
        {
            Ui.Dialog(this, "Zipper 설정");
            var grid = Ui.Grid(1);

            var exts = new CheckedListBox { MultiColumn = true, ColumnWidth = 78, Width = 480, Height = 120, CheckOnClick = true, BorderStyle = BorderStyle.FixedSingle };
            foreach (var e in Shell.Extensions) exts.Items.Add(e, Shell.IsAssociated(e));
            var all = new LinkLabel { Text = "모두 선택", AutoSize = true };
            all.LinkClicked += (s, e) => { for (int i = 0; i < exts.Items.Count; i++) exts.SetItemChecked(i, true); };
            var none = new LinkLabel { Text = "모두 해제", AutoSize = true };
            none.LinkClicked += (s, e) => { for (int i = 0; i < exts.Items.Count; i++) exts.SetItemChecked(i, false); };
            var winSettings = new LinkLabel { Text = "Windows 기본 앱 설정 열기", AutoSize = true };
            winSettings.LinkClicked += (s, e) =>
            {
                try { Process.Start("ms-settings:defaultapps?registeredAppUser=Zipper"); }
                catch { Process.Start("ms-settings:defaultapps"); }
            };
            var links = new FlowLayoutPanel { AutoSize = true, Margin = new Padding(0, 4, 0, 0) };
            links.Controls.AddRange(new Control[] { all, none, winSettings });

            var menu = new CheckBox { Text = "탐색기 오른쪽 클릭 메뉴에 Zipper 추가", AutoSize = true, Checked = Shell.ContextMenuEnabled, Margin = new Padding(3, 14, 3, 3) };
            var overwrite = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 180 };
            overwrite.Items.AddRange(ExtractForm.OverwriteNames);
            overwrite.SelectedIndex = Shell.OverwriteMode;
            var open = new CheckBox { Text = "압축을 푼 뒤 폴더 열기", AutoSize = true, Checked = Shell.OpenFolderAfterExtract };
            var owRow = new FlowLayoutPanel { AutoSize = true, Margin = Padding.Empty };
            owRow.Controls.AddRange(new Control[] { Ui.Label("같은 파일이 있으면"), overwrite });

            grid.Controls.Add(new Label { Text = "Zipper로 열 파일 형식", AutoSize = true, Font = new Font(Ui.Font, FontStyle.Bold) });
            grid.Controls.Add(exts);
            grid.Controls.Add(links);
            grid.Controls.Add(new Label
            {
                AutoSize = true, MaximumSize = new Size(480, 0), ForeColor = SystemColors.GrayText, Margin = new Padding(3, 6, 3, 3),
                Text = "Windows 10/11은 사용자가 직접 확정해야 기본 앱이 바뀝니다.\n저장한 뒤 압축 파일을 처음 열 때 나오는 창에서 Zipper와 '항상'을 고르거나,\n위 링크에서 Zipper를 기본 앱으로 지정하세요.",
            });
            grid.Controls.Add(menu);
            grid.Controls.Add(owRow);
            grid.Controls.Add(open);

            var ok = Ui.Button("저장");
            ok.Click += (s, e) =>
            {
                try
                {
                    Shell.Apply(exts.CheckedItems.Cast<string>(), menu.Checked);
                    Shell.OverwriteMode = overwrite.SelectedIndex;
                    Shell.OpenFolderAfterExtract = open.Checked;
                    DialogResult = DialogResult.OK;
                }
                catch (Exception ex) { Ui.Error(this, "설정을 저장하지 못했습니다.\n" + ex.Message); }
            };
            grid.Controls.Add(Ui.Buttons(ok, Ui.Button("취소", DialogResult.Cancel)));
            Controls.Add(grid);
        }
    }
}
