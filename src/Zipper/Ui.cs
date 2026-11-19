using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Text;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace Zipper
{
    static class Ui
    {
        public static readonly Font Font = new Font("Malgun Gothic", 9f);
        public static readonly Color Accent = Color.FromArgb(30, 110, 220);
        public static Icon AppIcon => _icon ?? (_icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath));
        static Icon _icon;

        static readonly string GlyphFont = new InstalledFontCollection().Families
            .Any(f => f.Name == "Segoe Fluent Icons") ? "Segoe Fluent Icons" : "Segoe MDL2 Assets";

        /// <summary>Renders an icon-font glyph to a bitmap, so the app ships no image files.</summary>
        public static Bitmap Glyph(char c, int size, Color color)
        {
            var bmp = new Bitmap(size, size);
            using (var g = Graphics.FromImage(bmp))
            using (var f = new Font(GlyphFont, size * 0.72f, GraphicsUnit.Pixel))
            using (var b = new SolidBrush(color))
            {
                g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
                g.DrawString(c.ToString(), f, b, new RectangleF(0, 0, size, size), sf);
            }
            return bmp;
        }

        public static int Scale(Control c, int px) => px * c.DeviceDpi / 96;

        // ---- shell file icons for the list view, cached per extension ----
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct SHFILEINFO
        {
            public IntPtr hIcon; public int iIcon; public uint dwAttributes;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szDisplayName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string szTypeName;
        }
        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        static extern IntPtr SHGetFileInfo(string path, uint attrs, ref SHFILEINFO info, uint size, uint flags);
        [DllImport("user32.dll")] static extern bool DestroyIcon(IntPtr h);

        static readonly Dictionary<string, string> TypeNames = new Dictionary<string, string>();

        /// <summary>Returns the image key for a file name, adding the icon to the list on first use.</summary>
        public static string IconKey(ImageList list, string name, bool dir)
        {
            var key = dir ? "<dir>" : Path.GetExtension(name).ToLowerInvariant();
            if (list.Images.ContainsKey(key)) return key;
            var info = new SHFILEINFO();
            // USEFILEATTRIBUTES: the file does not need to exist | ICON | SMALLICON | TYPENAME
            SHGetFileInfo(dir ? "folder" : "x" + key, dir ? 0x10u : 0x80u, ref info, (uint)Marshal.SizeOf(info), 0x10 | 0x100 | 0x1 | 0x400);
            if (info.hIcon != IntPtr.Zero)
            {
                list.Images.Add(key, Icon.FromHandle(info.hIcon).ToBitmap());
                DestroyIcon(info.hIcon);
            }
            TypeNames[key] = info.szTypeName;
            return key;
        }

        public static string TypeName(string key) => TypeNames.TryGetValue(key, out var t) ? t : "";

        public static string Size(long b)
        {
            string[] u = { "B", "KB", "MB", "GB", "TB" };
            double v = b; int i = 0;
            while (v >= 1024 && i < u.Length - 1) { v /= 1024; i++; }
            return i == 0 ? $"{b:N0} B" : $"{v:0.##} {u[i]}";
        }

        // ---- dialog helpers ----
        public static void Dialog(Form f, string title)
        {
            f.Text = title; f.Font = Font; f.Icon = AppIcon;
            f.FormBorderStyle = FormBorderStyle.FixedDialog;
            f.MaximizeBox = f.MinimizeBox = false;
            f.AutoSize = true; f.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            f.Padding = new Padding(14);
            f.BackColor = SystemColors.Window;
            f.KeyPreview = true;
            f.KeyDown += (s, e) => { if (e.KeyCode == Keys.Escape) f.DialogResult = DialogResult.Cancel; };
            // forms are laid out in 96-dpi pixels; scale fixed widths once for the real DPI
            f.Load += (s, e) => { var k = f.DeviceDpi / 96f; if (k != 1f) f.Scale(new SizeF(k, k)); };
        }

        /// <summary>Shows a dialog centered on its owner, or on screen with a taskbar button when started from Explorer.</summary>
        public static DialogResult Show(Form f, IWin32Window owner)
        {
            f.StartPosition = owner == null ? FormStartPosition.CenterScreen : FormStartPosition.CenterParent;
            f.ShowInTaskbar = owner == null;
            if (owner == null) f.TopMost = true; // started from Explorer: do not hide behind it
            return f.ShowDialog(owner);
        }

        public static TableLayoutPanel Grid(int cols) => new TableLayoutPanel
        {
            ColumnCount = cols, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Dock = DockStyle.Fill,
        };

        public static Label Label(string text) => new Label { Text = text, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 8, 10, 3) };

        public static Button Button(string text, DialogResult result = DialogResult.None) => new Button
        {
            Text = text, DialogResult = result, AutoSize = true, MinimumSize = new Size(88, 30), Margin = new Padding(6, 12, 0, 0),
        };

        public static FlowLayoutPanel Buttons(params Button[] buttons)
        {
            var p = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Dock = DockStyle.Fill, Margin = Padding.Empty };
            p.Controls.AddRange(buttons.Reverse().ToArray());
            return p;
        }

        public static void Error(IWin32Window owner, string msg) =>
            MessageBox.Show(owner, msg, "Zipper", MessageBoxButtons.OK, MessageBoxIcon.Warning);

        /// <summary>Asks for a password. Returns null if cancelled.</summary>
        public static string AskPassword(IWin32Window owner, string archive, bool wrong)
        {
            using (var f = new Form())
            {
                Dialog(f, "암호 입력");
                var grid = Grid(1);
                var box = new TextBox { UseSystemPasswordChar = true, Width = 320, Margin = new Padding(3, 6, 3, 3) };
                var show = new CheckBox { Text = "암호 표시", AutoSize = true };
                show.CheckedChanged += (s, e) => box.UseSystemPasswordChar = !show.Checked;
                var ok = Button("확인", DialogResult.OK);
                grid.Controls.Add(new Label
                {
                    AutoSize = true, MaximumSize = new Size(340, 0), ForeColor = wrong ? Color.Firebrick : SystemColors.ControlText,
                    Text = (wrong ? "암호가 틀렸습니다. 다시 입력하세요.\n" : "") + Path.GetFileName(archive) + " 의 암호를 입력하세요.",
                });
                grid.Controls.Add(box);
                grid.Controls.Add(show);
                grid.Controls.Add(Buttons(ok, Button("취소", DialogResult.Cancel)));
                f.Controls.Add(grid);
                f.AcceptButton = ok;
                return Show(f, owner) == DialogResult.OK ? box.Text : null;
            }
        }
    }
}
