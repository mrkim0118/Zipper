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
        readonly long total;

        /// <summary>"2/3" while a selection of several archives is processed; shown in every progress window's title.</summary>
        public static string Batch;

        /// <summary>Displays progress and cancellation for the supplied archive operation.</summary>
        ProgressForm(string title, Func<Action<int, string>, Func<bool>, SevenZip.Result> operation, long total)
        {
            this.total = total;
            if (Batch != null) title += $"  ({Batch})";
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
                info.Text = Info(percent, watch.Elapsed);
            };
            Shown += (s, e) =>
            {
                timer.Start();
                new Thread(() =>
                {
                    SevenZip.Result r;
                    try { r = operation((p, f) => { percent = p; if (f.Length > 0) current = f; }, () => cancel); }
                    catch (Exception ex) { r = new SevenZip.Result { ExitCode = SevenZip.Result.FatalError, Output = "ERROR: " + ex.Message }; }
                    BeginInvoke((Action)(() => { result = r; timer.Stop(); Close(); }));
                }) { IsBackground = true }.Start();
            };
            FormClosing += (s, e) => { if (result == null) { e.Cancel = true; cancel = true; } }; // X = cancel
        }

        /// <summary>"42% · 12.3 MB/s · 남은 시간 00:01:10 · 경과 시간 00:00:51". Speed needs the total size.</summary>
        string Info(int p, TimeSpan t)
        {
            var parts = new List<string> { $"{p}%" };
            double secs = t.TotalSeconds;
            if (total > 0 && secs >= 1 && p > 0) parts.Add(Ui.Size((long)(total * (p / 100.0) / secs)) + "/s");
            if (secs >= 2 && p >= 1 && p < 100)
                parts.Add("남은 시간 " + TimeSpan.FromSeconds(Math.Round(secs * (100 - p) / p)).ToString(@"hh\:mm\:ss"));
            parts.Add("경과 시간 " + t.ToString(@"hh\:mm\:ss"));
            return string.Join("   ·   ", parts);
        }

        /// <summary>Runs a 7-Zip command with the progress dialog. total = bytes processed in all (for the speed).</summary>
        public static SevenZip.Result Run(IWin32Window owner, string title, IList<string> args, string password = null, long total = 0)
        {
            return Run(owner, title, (progress, cancelled) => SevenZip.Run(args, password, progress, cancelled), total);
        }

        /// <summary>Runs archive preparation and compression on the same cancellable worker.</summary>
        public static SevenZip.Result Run(IWin32Window owner, string title, Func<Action<int, string>, Func<bool>, SevenZip.Result> operation, long total = 0)
        {
            using (ProgressForm f = new ProgressForm(title, operation, total))
            {
                Ui.Show(f, owner);
                return f.result;
            }
        }
    }
}
