using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Threading;
using System.Windows.Forms;

namespace Zipper
{
    /// <summary>Runs one 7z command on a worker thread and shows its progress with a cancel button.</summary>
    class ProgressForm : Form
    {
        readonly ProgressBar bar = new ProgressBar { Width = 420, Height = 18, Maximum = 100, Margin = new Padding(3, 8, 3, 3) };
        readonly Label file = new Label { AutoSize = false, Width = 420, Height = 20, AutoEllipsis = true, ForeColor = SystemColors.GrayText };
        readonly Label info = new Label { AutoSize = true };
        readonly Stopwatch watch = Stopwatch.StartNew();
        volatile int percent; volatile string current = "";
        volatile bool cancel;
        SevenZip.Result result;

        ProgressForm(string title, IList<string> args, string password, string workDir)
        {
            Ui.Dialog(this, title);
            ControlBox = true;
            var grid = Ui.Grid(1);
            var head = new Label { Text = title, AutoSize = true, Font = new Font(Ui.Font.FontFamily, 11f, FontStyle.Bold) };
            var stop = Ui.Button("취소");
            stop.Click += (s, e) => { cancel = true; stop.Enabled = false; };
            grid.Controls.AddRange(new Control[] { head, file, bar, info, Ui.Buttons(stop) });
            Controls.Add(grid);

            var timer = new System.Windows.Forms.Timer { Interval = 150 };
            timer.Tick += (s, e) =>
            {
                bar.Value = Math.Min(100, percent);
                file.Text = current;
                var t = watch.Elapsed;
                info.Text = $"{percent}%   ·   경과 시간 {t:hh\\:mm\\:ss}";
            };
            Shown += (s, e) =>
            {
                timer.Start();
                new Thread(() =>
                {
                    var r = SevenZip.Run(args, password, (p, f) => { percent = p; if (f.Length > 0) current = f; }, () => cancel, workDir);
                    BeginInvoke((Action)(() => { result = r; timer.Stop(); Close(); }));
                }) { IsBackground = true }.Start();
            };
            FormClosing += (s, e) => { if (result == null) { e.Cancel = true; cancel = true; } }; // X = cancel
        }

        public static SevenZip.Result Run(IWin32Window owner, string title, IList<string> args, string password = null, string workDir = null)
        {
            using (var f = new ProgressForm(title, args, password, workDir))
            {
                Ui.Show(f, owner);
                return f.result;
            }
        }
    }
}
