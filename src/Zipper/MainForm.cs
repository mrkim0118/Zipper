using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows.Forms;

namespace Zipper
{
    /// <summary>Archive browser: lists the contents of one archive like a folder.</summary>
    class MainForm : Form
    {
        class Node
        {
            public string Name, FullPath;
            public bool IsDir;
            public long Size, Packed;
            public DateTime? Modified;
            public Node Parent;
            public readonly Dictionary<string, Node> Kids = new Dictionary<string, Node>(StringComparer.OrdinalIgnoreCase);
        }

        string archive, password, type;
        Node root, current;
        int sortColumn; bool sortDesc;
        bool draggingOut;
        readonly string tempRoot = Path.Combine(Path.GetTempPath(), "Zipper", Process.GetCurrentProcess().Id.ToString());

        readonly ListView list = new ListView
        {
            View = View.Details, Dock = DockStyle.Fill, FullRowSelect = true, BorderStyle = BorderStyle.None,
            AllowDrop = true, HideSelection = false,
        };
        readonly ImageList icons = new ImageList { ColorDepth = ColorDepth.Depth32Bit };
        readonly ToolStrip tools = new ToolStrip { GripStyle = ToolStripGripStyle.Hidden, RenderMode = ToolStripRenderMode.System, Padding = new Padding(6, 4, 6, 4) };
        readonly ToolStrip nav = new ToolStrip { GripStyle = ToolStripGripStyle.Hidden, RenderMode = ToolStripRenderMode.System, Padding = new Padding(6, 2, 6, 2) };
        readonly ToolStripLabel pathLabel = new ToolStripLabel();
        readonly ToolStripTextBox search = new ToolStripTextBox { Alignment = ToolStripItemAlignment.Right, ToolTipText = "압축 파일 안에서 이름으로 찾기 (Ctrl+F, 지우기: Esc)" };
        string Filter => search.Text.Trim();
        readonly ToolStripStatusLabel status = new ToolStripStatusLabel { Spring = true, TextAlign = ContentAlignment.MiddleLeft };
        readonly Control empty;
        readonly FlowLayoutPanel recentPanel = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.TopDown, AutoSize = true, Anchor = AnchorStyles.None, Margin = new Padding(3, 18, 3, 3), WrapContents = false,
        };

        /// <summary>Up to five recent archives as links under the drop hint.</summary>
        void FillRecent()
        {
            recentPanel.Controls.Clear();
            var recent = Shell.Recent.Take(5).ToArray();
            if (recent.Length == 0) return;
            recentPanel.Controls.Add(new Label { Text = "최근에 연 압축 파일", AutoSize = true, ForeColor = SystemColors.GrayText, Margin = new Padding(3, 3, 3, 6) });
            foreach (var r in recent)
            {
                var link = new LinkLabel { Text = Path.GetFileName(r), AutoSize = true, Margin = new Padding(3, 2, 3, 2) };
                new ToolTip().SetToolTip(link, r);
                link.LinkClicked += (s, e) => OpenArchive(r);
                recentPanel.Controls.Add(link);
            }
        }
        ToolStripButton btnExtract, btnAdd, btnDelete, btnTest, btnUp;

        public MainForm(string open)
        {
            Text = "Zipper";
            Font = Ui.Font; Icon = Ui.AppIcon;
            ClientSize = new Size(900, 560);
            MinimumSize = new Size(560, 360);
            StartPosition = FormStartPosition.CenterScreen;
            AllowDrop = true;
            KeyPreview = true;

            int big = Ui.Scale(this, 32), small = Ui.Scale(this, 16);
            tools.ImageScalingSize = new Size(big, big);
            nav.ImageScalingSize = new Size(small, small);
            icons.ImageSize = new Size(small, small);
            list.SmallImageList = icons;
            ToolStripButton Tool(string text, Image image, string tip, Action click)
            {
                var b = new ToolStripButton(text, image, (s, e) => click())
                {
                    TextImageRelation = TextImageRelation.ImageAboveText, ToolTipText = tip, Padding = new Padding(8, 2, 8, 2), AutoSize = true,
                };
                tools.Items.Add(b);
                return b;
            }
            ToolStripButton GlyphTool(string text, char glyph, Color color, string tip, Action click) => Tool(text, Ui.Glyph(glyph, big, color), tip, click);
            var openBtn = new ToolStripSplitButton("열기", Ui.Glyph('', big, Color.FromArgb(232, 163, 23)))
            {
                TextImageRelation = TextImageRelation.ImageAboveText, ToolTipText = "압축 파일 열기 (Ctrl+O), 화살표: 최근 파일", Padding = new Padding(8, 2, 8, 2), AutoSize = true,
            };
            openBtn.ButtonClick += (s, e) => OpenDialog();
            openBtn.DropDownItems.Add("(없음)"); // keeps the arrow active; filled when opened
            openBtn.DropDownOpening += (s, e) =>
            {
                openBtn.DropDownItems.Clear();
                foreach (var r in Shell.Recent) openBtn.DropDownItems.Add(Path.GetFileName(r), null, (o, a) => OpenArchive(r)).ToolTipText = r;
                if (openBtn.DropDownItems.Count == 0) openBtn.DropDownItems.Add("최근에 연 압축 파일이 없습니다").Enabled = false;
            };
            tools.Items.Add(openBtn);
            btnExtract = Tool("압축 풀기", Ui.ArchiveIcon(big, true), "선택한 항목 또는 전체 풀기 (Ctrl+E)", ExtractSelected);
            Tool("새로 압축", Ui.ArchiveIcon(big, false), "파일을 골라 새 압축 파일 만들기 (Ctrl+N)", NewArchive);
            tools.Items.Add(new ToolStripSeparator());
            btnAdd = GlyphTool("파일 추가", '', Ui.Accent, "열린 압축 파일에 파일 추가", AddDialog);
            btnDelete = GlyphTool("삭제", '', Color.FromArgb(214, 60, 60), "선택한 항목을 압축 파일에서 삭제 (Delete)", DeleteSelected);
            btnTest = GlyphTool("테스트", '', Color.FromArgb(0, 140, 150), "압축 파일 손상 검사", TestArchive);
            var settings = GlyphTool("설정", '', Color.FromArgb(110, 110, 110), "파일 연결과 탐색기 메뉴 설정", ShowSettings);
            settings.Alignment = ToolStripItemAlignment.Right;

            btnUp = new ToolStripButton("", Ui.Glyph('', small, SystemColors.ControlText), (s, e) => GoUp()) { ToolTipText = "상위 폴더 (Backspace)" };
            nav.Items.Add(btnUp);
            nav.Items.Add(pathLabel);
            search.Size = new Size(Ui.Scale(this, 200), search.Height);
            search.TextChanged += (s, e) => { Fill(); SyncButtons(); };
            search.KeyDown += (s, e) =>
            {
                if (e.KeyCode == Keys.Escape) { search.Text = ""; list.Focus(); e.Handled = e.SuppressKeyPress = true; }
                else if (e.KeyCode == Keys.Down || e.KeyCode == Keys.Enter) { list.Focus(); if (list.Items.Count > 0 && list.SelectedItems.Count == 0) list.Items[0].Selected = true; e.SuppressKeyPress = true; }
            };
            nav.Items.Add(search);
            nav.Items.Add(new ToolStripLabel("검색") { Alignment = ToolStripItemAlignment.Right, ForeColor = SystemColors.GrayText });

            list.Columns.Add("이름", 340);
            list.Columns.Add("크기", 100, HorizontalAlignment.Right);
            list.Columns.Add("압축 크기", 100, HorizontalAlignment.Right);
            list.Columns.Add("종류", 150);
            list.Columns.Add("수정한 날짜", 150);
            foreach (ColumnHeader c in list.Columns) c.Width = Ui.Scale(this, c.Width);
            list.ItemActivate += (s, e) => OpenItem(list.SelectedItems.Count == 1 ? (Node)list.SelectedItems[0].Tag : null);
            list.ColumnClick += (s, e) => { sortDesc = sortColumn == e.Column && !sortDesc; sortColumn = e.Column; Fill(); };
            list.ItemDrag += (s, e) => DragOut();
            list.SelectedIndexChanged += (s, e) => SyncButtons();
            list.ContextMenuStrip = BuildMenu();
            list.LabelEdit = true;
            list.BeforeLabelEdit += (s, e) => { if (!CanRename) e.CancelEdit = true; };
            list.AfterLabelEdit += (s, e) => { e.CancelEdit = true; if (e.Label != null) Rename((Node)list.Items[e.Item].Tag, e.Label.Trim()); };

            var strip = new StatusStrip { SizingGrip = true };
            strip.Items.Add(status);

            empty = BuildEmpty();
            var body = new Panel { Dock = DockStyle.Fill };
            body.Controls.Add(list);
            body.Controls.Add(empty);
            Controls.Add(body);
            Controls.Add(nav);
            Controls.Add(tools);
            Controls.Add(strip);

            foreach (Control c in new Control[] { this, list, empty })
            {
                c.DragEnter += (s, e) => e.Effect = !draggingOut && e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
                c.DragDrop += (s, e) => Dropped((string[])e.Data.GetData(DataFormats.FileDrop));
            }
            KeyDown += OnKey;
            FormClosed += (s, e) => { try { Directory.Delete(tempRoot, true); } catch { } }; // files still open in a viewer stay behind

            Load += (s, e) => { if (open != null) OpenArchive(open); else ShowArchive(); };
        }

        // ---------- layout pieces ----------
        Control BuildEmpty()
        {
            var t = new TableLayoutPanel { Dock = DockStyle.Fill, BackColor = SystemColors.Window, ColumnCount = 1, RowCount = 5, AllowDrop = true };
            t.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
            t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            t.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
            var pic = new PictureBox { Image = Ui.Glyph('', Ui.Scale(this, 72), Color.FromArgb(190, 200, 215)), SizeMode = PictureBoxSizeMode.AutoSize, Anchor = AnchorStyles.None };
            var label = new Label
            {
                AutoSize = true, Anchor = AnchorStyles.None, ForeColor = SystemColors.GrayText, TextAlign = ContentAlignment.MiddleCenter,
                Font = new Font(Ui.Font.FontFamily, 11f), Margin = new Padding(3, 12, 3, 3),
                Text = "압축 파일을 여기로 끌어 놓으면 열립니다.\n다른 파일이나 폴더를 끌어 놓으면 새로 압축합니다.",
            };
            t.Controls.Add(new Label(), 0, 0);
            t.Controls.Add(pic, 0, 1);
            t.Controls.Add(label, 0, 2);
            t.Controls.Add(recentPanel, 0, 3);
            return t;
        }

        ContextMenuStrip BuildMenu()
        {
            var m = new ContextMenuStrip();
            var open = m.Items.Add("열기", null, (s, e) => OpenItem(list.SelectedItems.Count == 1 ? (Node)list.SelectedItems[0].Tag : null));
            open.Font = new Font(m.Font, FontStyle.Bold);
            m.Items.Add("압축 풀기...", null, (s, e) => ExtractSelected());
            m.Items.Add("압축 파일 위치에 풀기", null, (s, e) => ExtractTo(Path.GetDirectoryName(archive)));
            m.Items.Add(new ToolStripSeparator());
            var ren = m.Items.Add("이름 바꾸기", null, (s, e) => BeginRename());
            ((ToolStripMenuItem)ren).ShortcutKeyDisplayString = "F2";
            var del = m.Items.Add("삭제", null, (s, e) => DeleteSelected());
            m.Opening += (s, e) =>
            {
                e.Cancel = archive == null || Selected().Count == 0;
                del.Enabled = btnDelete.Enabled;
                ren.Enabled = CanRename;
            };
            return m;
        }

        void OnKey(object s, KeyEventArgs e)
        {
            if (e.Control && e.KeyCode == Keys.O) OpenDialog();
            else if (e.Control && e.KeyCode == Keys.N) NewArchive();
            else if (e.Control && e.KeyCode == Keys.E && archive != null) ExtractSelected();
            else if (e.Control && e.KeyCode == Keys.A) foreach (ListViewItem i in list.Items) i.Selected = i.Text != "..";
            else if (e.KeyCode == Keys.Back && list.Focused) GoUp();
            else if (e.KeyCode == Keys.Delete && list.Focused && btnDelete.Enabled) DeleteSelected();
            else if (e.KeyCode == Keys.F5 && archive != null) Reload();
            else if (e.KeyCode == Keys.F2 && list.Focused && CanRename) BeginRename();
            else if (e.Control && e.KeyCode == Keys.F && archive != null) { search.Focus(); search.SelectAll(); }
            else if (e.KeyCode == Keys.Escape && Filter.Length > 0) search.Text = "";
            else return;
            e.Handled = true;
        }

        // ---------- archive loading ----------
        void OpenArchive(string path)
        {
            path = Path.GetFullPath(path);
            string pw = null;
            Cursor = Cursors.WaitCursor;
            try
            {
                if (!Ops.Open(this, path, ref pw, out var entries, out var t)) return;
                archive = path; password = pw; type = t;
                try { Shell.AddRecent(path); } catch { } // a full or locked registry must not block opening
                search.Text = "";
                Build(entries);
                current = root;
                ShowArchive();
            }
            finally { Cursor = Cursors.Default; }
        }

        void Reload()
        {
            var at = current?.FullPath;
            var entries = SevenZip.List(archive, password, out var r, out type);
            if (!r.Ok && entries.Count == 0) { Ui.Error(this, "압축 파일을 다시 읽지 못했습니다.\n\n" + r.Errors); return; }
            Build(entries);
            current = root;
            foreach (var part in (at ?? "").Split(new[] { '\\' }, StringSplitOptions.RemoveEmptyEntries))
                if (current.Kids.TryGetValue(part, out var k) && k.IsDir) current = k; else break;
            ShowArchive();
        }

        void Build(List<SevenZip.Entry> entries)
        {
            root = new Node { Name = "", FullPath = "", IsDir = true };
            foreach (var e in entries)
            {
                var parts = e.Path.Split(new[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries);
                var n = root;
                for (int i = 0; i < parts.Length; i++)
                {
                    if (!n.Kids.TryGetValue(parts[i], out var k))
                    {
                        k = new Node { Name = parts[i], Parent = n, IsDir = true, FullPath = string.Join("\\", parts, 0, i + 1) };
                        n.Kids[parts[i]] = k;
                    }
                    n = k;
                }
                if (n == root) continue;
                n.IsDir = e.IsDir;
                n.FullPath = e.Path; // keep 7z's exact spelling for extraction
                n.Size = e.Size; n.Packed = e.Packed; n.Modified = e.Modified;
            }
            // a "file" that turned out to have children is a folder
            void Fix(Node n) { foreach (var k in n.Kids.Values) { if (k.Kids.Count > 0) k.IsDir = true; Fix(k); } }
            Fix(root);
            var all = Flatten(root).ToList();
            status.Text = $"파일 {all.Count(x => !x.IsDir):N0}개, 폴더 {all.Count(x => x.IsDir):N0}개   ·   원본 {Ui.Size(all.Sum(x => x.Size))}"
                        + $"   ·   압축 {Ui.Size(ArchiveBytes())}   ·   {type}";
        }

        /// <summary>Size on disk, counting every volume of a split archive (a.7z.001…, a.part1.rar…, a.zip + a.z01…, a.vol1.egg…, a.alz + a.a00…).</summary>
        long ArchiveBytes()
        {
            const string volume = @"(\.\d{3}|\.part\d+\.rar|\.zip|\.z\d\d|\.vol\d+\.egg|\.alz|\.a\d\d)$";
            var name = Path.GetFileName(archive);
            var stem = Regex.Replace(name, volume, "", RegexOptions.IgnoreCase);
            if (stem == name) return new FileInfo(archive).Length;
            var rx = new Regex("^" + Regex.Escape(stem) + volume, RegexOptions.IgnoreCase);
            return Directory.GetFiles(Path.GetDirectoryName(archive), stem + ".*")
                .Where(f => rx.IsMatch(Path.GetFileName(f))).Sum(f => new FileInfo(f).Length);
        }

        static IEnumerable<Node> Flatten(Node n) => n.Kids.Values.SelectMany(k => new[] { k }.Concat(Flatten(k)));

        void ShowArchive()
        {
            bool has = archive != null;
            empty.Visible = !has;
            list.Visible = has;
            nav.Visible = has;
            Text = has ? Path.GetFileName(archive) + " - Zipper" : "Zipper";
            if (!has) { status.Text = "준비"; FillRecent(); }
            Fill();
            SyncButtons();
        }

        void Fill()
        {
            list.BeginUpdate();
            list.Items.Clear();
            if (current != null)
            {
                // searching lists matches from the whole archive with their folder path; otherwise the current folder
                bool searching = Filter.Length > 0;
                var shown = searching
                    ? Flatten(root).Where(n => n.Name.Normalize().IndexOf(Filter, StringComparison.CurrentCultureIgnoreCase) >= 0)
                    : current.Kids.Values;
                pathLabel.Text = searching ? $"\"{Filter}\" 검색 결과: {shown.Count():N0}개"
                    : Path.GetFileName(archive) + (current == root ? "" : "  ›  " + current.FullPath.Normalize().Replace("\\", "  ›  ")); // "\" renders as ₩ in Korean fonts
                if (current != root && !searching) list.Items.Add(new ListViewItem("..", Ui.IconKey(icons, "", true)) { Tag = current.Parent });
                Func<Node, IComparable> key = sortColumn == 1 ? n => n.Size : sortColumn == 2 ? n => n.Packed
                    : sortColumn == 3 ? n => Ui.TypeName(Ui.IconKey(icons, n.Name, n.IsDir)) : sortColumn == 4 ? n => n.Modified ?? DateTime.MinValue
                    : (Func<Node, IComparable>)(n => n.Name.ToLowerInvariant());
                var kids = shown.OrderBy(n => n.IsDir ? 0 : 1);
                kids = sortDesc ? kids.ThenByDescending(key) : kids.ThenBy(key);
                foreach (var n in kids)
                {
                    var ik = Ui.IconKey(icons, n.Name, n.IsDir);
                    list.Items.Add(new ListViewItem(new[]
                    {
                        // search results show the folder path; "\" would render as ₩ in Korean fonts
                        (searching ? n.FullPath.Replace("\\", "  ›  ") : n.Name).Normalize(), n.IsDir ? "" : Ui.Size(n.Size), n.IsDir ? "" : Ui.Size(n.Packed),
                        Ui.TypeName(ik), n.Modified?.ToString("yyyy-MM-dd HH:mm") ?? "",
                    }, ik) { Tag = n });
                }
            }
            list.EndUpdate();
            btnUp.Enabled = current != null && (current != root || Filter.Length > 0);
        }

        bool Updatable => archive != null && Regex.IsMatch(type ?? "", "^(zip|7z|tar|wim)$", RegexOptions.IgnoreCase)
                          && !Regex.IsMatch(archive, @"\.(\d{3}|z\d\d)$", RegexOptions.IgnoreCase);

        void SyncButtons()
        {
            btnExtract.Enabled = btnTest.Enabled = archive != null;
            btnAdd.Enabled = Updatable;
            btnDelete.Enabled = Updatable && Selected().Count > 0;
        }

        List<Node> Selected() => list.SelectedItems.Cast<ListViewItem>().Where(i => i.Text != "..").Select(i => (Node)i.Tag).ToList();

        // ---------- rename ----------
        /// <summary>One item, in an archive 7-Zip can update, outside the search results (they show full paths).</summary>
        bool CanRename => Updatable && Filter.Length == 0 && Selected().Count == 1;

        void BeginRename()
        {
            var item = list.SelectedItems.Cast<ListViewItem>().FirstOrDefault(i => i.Text != "..");
            item?.BeginEdit();
        }

        void Rename(Node n, string name)
        {
            if (name.Length == 0 || name == n.Name) return;
            if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name == "." || name == "..")
            {
                Ui.Error(this, "파일 이름에 쓸 수 없는 문자가 있습니다: \\ / : * ? \" < > |");
                return;
            }
            if (n.Parent.Kids.TryGetValue(name, out var other) && other != n)
            {
                Ui.Error(this, $"같은 이름의 항목이 이미 있습니다: {name}");
                return;
            }
            var parent = n.FullPath.Contains('\\') ? n.FullPath.Substring(0, n.FullPath.LastIndexOf('\\') + 1) : "";
            // 7-Zip renames a folder's contents along with it
            var r = ProgressForm.Run(this, "이름 바꾸는 중", new[] { "rn", archive, n.FullPath, parent + name }, password ?? "");
            if (!r.Ok && !r.Cancelled) Ui.Error(this, "이름을 바꾸지 못했습니다.\n\n" + r.Errors);
            Reload();
            var renamed = list.Items.Cast<ListViewItem>().FirstOrDefault(i => i.Text == name.Normalize());
            if (renamed != null) { renamed.Selected = renamed.Focused = true; renamed.EnsureVisible(); }
        }

        // ---------- navigation ----------
        void OpenItem(Node n)
        {
            if (n == null) return;
            if (n.IsDir) { current = n; if (Filter.Length > 0) search.Text = ""; Fill(); SyncButtons(); return; }
            var dir = Path.Combine(tempRoot, Guid.NewGuid().ToString("N").Substring(0, 8));
            if (!Ops.Extract(this, archive, dir, new[] { n.FullPath }, password, false, "-aoa", flat: true)) return;
            try { Process.Start(new ProcessStartInfo(Ops.OnDisk(Path.Combine(dir, n.Name))) { UseShellExecute = true }); }
            catch (Exception ex) { Ui.Error(this, ex.Message); }
        }

        void GoUp()
        {
            if (Filter.Length > 0) { search.Text = ""; return; } // first leave the search results
            if (current?.Parent == null) return;
            var from = current;
            current = current.Parent;
            Fill(); SyncButtons();
            var item = list.Items.Cast<ListViewItem>().FirstOrDefault(i => i.Tag == from);
            if (item != null) { item.Selected = item.Focused = true; item.EnsureVisible(); }
        }

        // ---------- commands ----------
        void OpenDialog()
        {
            using (var d = new OpenFileDialog { Title = "압축 파일 열기", Filter = "압축 파일|" + string.Join(";", Shell.Extensions.Select(e => "*." + e)) + ";*.z0?;*.r0?|모든 파일|*.*" })
                if (d.ShowDialog(this) == DialogResult.OK) OpenArchive(d.FileName);
        }

        void ExtractSelected()
        {
            if (archive == null) return;
            var sel = Selected();
            using (var f = new ExtractForm(archive, sel.Count))
                if (f.ShowDialog(this) == DialogResult.OK)
                    Ops.Extract(this, archive, f.Dest, sel.Select(n => n.FullPath).ToList(), password, f.OpenFolder, f.OverwriteSwitch, total: Bytes(sel));
        }

        void ExtractTo(string dest) =>
            Ops.Extract(this, archive, dest, Selected().Select(n => n.FullPath).ToList(), password, Shell.OpenFolderAfterExtract, total: Bytes(Selected()));

        /// <summary>Uncompressed bytes of the selection (everything when nothing is selected), for the speed display.</summary>
        long Bytes(List<Node> sel) => (sel.Count == 0 ? new List<Node> { root } : sel).Sum(n => n.IsDir ? Flatten(n).Sum(k => k.Size) : n.Size);

        void NewArchive()
        {
            using (var d = new OpenFileDialog { Title = "압축할 파일 선택 (폴더는 창으로 끌어 놓으세요)", Multiselect = true })
                if (d.ShowDialog(this) == DialogResult.OK) Compress(d.FileNames);
        }

        void Compress(string[] files)
        {
            using (var f = new CompressForm(files, "zip"))
            {
                if (f.ShowDialog(this) != DialogResult.OK) return;
                if (!Ops.Compress(this, f.Files, f.Options)) return;
                var made = File.Exists(f.Options.Output) ? f.Options.Output : f.Options.Output + ".001";
                if (File.Exists(made)) OpenArchive(made);
            }
        }

        void AddDialog()
        {
            using (var d = new OpenFileDialog { Title = "압축 파일에 추가할 파일", Multiselect = true })
                if (d.ShowDialog(this) == DialogResult.OK) AddFiles(d.FileNames);
        }

        /// <summary>Adds the selected files using the same entry names as a new archive.</summary>
        void AddFiles(string[] files)
        {
            List<string> args = new List<string> { "a" };
            if (password != null) args.Add("-p");
            HashSet<string> existing = new HashSet<string>(Flatten(root).Select(n => n.FullPath.Replace('/', '\\')), StringComparer.OrdinalIgnoreCase);
            SevenZip.Result r = ProgressForm.Run(this, "추가하는 중", (progress, cancelled) =>
                SevenZip.Add(args, archive, files, password ?? "", progress, cancelled, existing, renameExe: Shell.RenameExe));
            if (!r.Ok && !r.Cancelled) Ui.Error(this, "파일을 추가하지 못했습니다.\n\n" + r.Errors);
            Reload();
        }

        void DeleteSelected()
        {
            var sel = Selected();
            if (sel.Count == 0 || !Updatable) return;
            if (MessageBox.Show(this, $"선택한 {sel.Count}개 항목을 압축 파일에서 삭제할까요?\n이 작업은 되돌릴 수 없습니다.", "Zipper",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
            var list = SevenZip.ListFile(sel.Select(n => n.FullPath));
            try
            {
                var r = ProgressForm.Run(this, "삭제하는 중", new[] { "d", archive, "-spd", "@" + list }, password ?? "");
                if (!r.Ok && !r.Cancelled) Ui.Error(this, "삭제하지 못했습니다.\n\n" + r.Errors);
            }
            finally { File.Delete(list); }
            Reload();
        }

        void TestArchive()
        {
            var r = ProgressForm.Run(this, "검사하는 중", new[] { "t", archive }, password ?? "");
            if (r.Cancelled) return;
            if (r.Ok) MessageBox.Show(this, "압축 파일에 이상이 없습니다.", "Zipper", MessageBoxButtons.OK, MessageBoxIcon.Information);
            else Ui.Error(this, "압축 파일에 문제가 있습니다.\n\n" + r.Errors);
        }

        void ShowSettings()
        {
            using (var f = new SettingsForm()) f.ShowDialog(this);
        }

        // ---------- drag & drop ----------
        void Dropped(string[] files)
        {
            if (files == null || files.Length == 0) return;
            bool isArchive = files.Length == 1 && File.Exists(files[0]) &&
                Regex.IsMatch(files[0], @"\.(" + string.Join("|", Shell.Extensions) + @"|z\d\d|r\d\d|\d{3})$", RegexOptions.IgnoreCase);
            if (archive != null && Updatable)
            {
                var ask = MessageBox.Show(this, $"{files.Length}개 항목을 {Path.GetFileName(archive)}에 추가할까요?" + (isArchive ? "\n(아니요: 끌어 놓은 압축 파일 열기)" : ""),
                    "Zipper", isArchive ? MessageBoxButtons.YesNoCancel : MessageBoxButtons.YesNo, MessageBoxIcon.Question);
                if (ask == DialogResult.Yes) { AddFiles(files); return; }
                if (ask != DialogResult.No || !isArchive) return;
            }
            if (isArchive) OpenArchive(files[0]);
            else BeginInvoke((Action)(() => Compress(files))); // leave the Explorer drag loop first
        }

        void DragOut()
        {
            var sel = Selected();
            if (sel.Count == 0) return;
            var dir = Path.Combine(tempRoot, Guid.NewGuid().ToString("N").Substring(0, 8));
            if (!Ops.Extract(this, archive, dir, sel.Select(n => n.FullPath).ToList(), password, false, "-aoa")) return;
            var paths = sel.Select(n => Ops.OnDisk(Path.Combine(dir, n.FullPath))).Where(p => File.Exists(p) || Directory.Exists(p)).ToArray();
            draggingOut = true;
            try { list.DoDragDrop(new DataObject(DataFormats.FileDrop, paths), DragDropEffects.Copy); }
            finally { draggingOut = false; }
        }
    }
}
