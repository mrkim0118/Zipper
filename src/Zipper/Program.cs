using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace Zipper
{
    static class Program
    {
        [STAThread]
        static void Main(string[] args)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            if (args.Length == 0 || !args[0].StartsWith("/")) Shell.EnsureContextMenu();
            if (args.Length == 0) { Application.Run(new MainForm(null)); return; }

            var cmd = args[0].ToLowerInvariant();
            var rest = args.Skip(1).ToArray();
            try
            {
                switch (cmd)
                {
                    case "/register": // installer: "/register assoc" and/or "/register menu"
                        bool all = rest.Length == 0;
                        Shell.Apply(all || rest.Contains("assoc") ? Shell.Extensions : Shell.Extensions.Where(Shell.IsAssociated),
                                    all || rest.Contains("menu") || Shell.ContextMenuEnabled);
                        return;
                    case "/unregister": Shell.Unregister(); return;
                    case "/restartexplorer": Shell.RestartExplorer(); return; // installer, after turning on the Windows 11 full menu
                    // Several selected archives are gathered into one process and handled one after another;
                    // the password that opened one archive is tried first on the next.
                    case "/xhere": case "/xfolder": case "/xsmart":
                    {
                        var how = cmd == "/xhere" ? Ops.Target.Here : cmd == "/xfolder" ? Ops.Target.Folder : Ops.Target.Smart;
                        string pw = null;
                        Batch(Collect(cmd, rest), a => Ops.ExtractQuick(a, how, ref pw));
                        return;
                    }
                    case "/t":
                    {
                        string pw = null;
                        Batch(Collect(cmd, rest), a => Ops.TestQuick(a, ref pw));
                        return;
                    }
                    case "/x":
                    {
                        string pw = null;
                        Batch(Collect(cmd, rest), a =>
                        {
                            if (!Ops.Open(null, a, ref pw, out var entries, out _)) return;
                            using (var f = new ExtractForm(a, 0))
                                if (Ui.Show(f, null) == DialogResult.OK)
                                    Ops.Extract(null, a, f.Dest, null, pw, f.OpenFolder, f.OverwriteSwitch, total: entries.Sum(e => e.Size), entries: entries);
                        });
                        return;
                    }
                    case "/c": case "/czip": case "/c7z":
                        var files = Collect(cmd, rest);
                        if (files == null || files.Length == 0) return; // another instance takes these files
                        if (cmd == "/c")
                        {
                            using (var f = new CompressForm(files, "zip"))
                                if (Ui.Show(f, null) == DialogResult.OK) Ops.Compress(null, f.Files, f.Options);
                        }
                        else
                        {
                            var fmt = cmd == "/czip" ? "zip" : "7z";
                            Ops.Compress(null, files, new CompressOptions { Output = Ops.DefaultOutput(files, fmt), Format = fmt, RenameExe = Shell.RenameExe });
                        }
                        return;
                    // /c:tar.gz, /c:xz ... any format the compress dialog offers; /ceach:zip makes one archive per item
                    case var c when c.StartsWith("/c:") || c.StartsWith("/ceach:"):
                        var target = c.Substring(c.IndexOf(':') + 1);
                        if (!CompressForm.Formats.Contains(target)) throw new ArgumentException("지원하지 않는 압축 형식입니다: " + target);
                        var items = Collect(cmd, rest);
                        if (items == null || items.Length == 0) return;
                        Ops.Compress(null, items, new CompressOptions
                        {
                            Output = Ops.DefaultOutput(items, target), Format = target, RenameExe = Shell.RenameExe, Each = c.StartsWith("/ceach:"),
                        });
                        return;
                    default:
                        Application.Run(new MainForm(args[0]));
                        return;
                }
            }
            catch (Exception ex)
            {
                Ui.Error(null, ex.Message);
            }
        }

        /// <summary>Runs the action for each item, numbering the progress windows ("2/3") when there are several.</summary>
        public static void Batch(string[] items, Action<string> action)
        {
            if (items == null) return; // another instance handles these
            try
            {
                for (int i = 0; i < items.Length; i++)
                {
                    ProgressForm.Batch = items.Length > 1 ? $"{i + 1}/{items.Length}" : null;
                    action(items[i]);
                }
            }
            finally { ProgressForm.Batch = null; }
        }

        /// <summary>
        /// Explorer starts one process per selected file for a classic verb. The first process collects the
        /// others' paths over a named pipe for a moment, so one archive is made from the whole selection.
        /// Returns null in the processes that handed their paths over.
        /// </summary>
        static string[] Collect(string verb, string[] files)
        {
            var name = "Zipper-" + verb.TrimStart('/') + "-" + Environment.UserName.GetHashCode().ToString("x");
            using (var mutex = new Mutex(true, @"Local\" + name, out bool first))
            {
                if (!first)
                {
                    try
                    {
                        using (var c = new NamedPipeClientStream(".", name, PipeDirection.Out))
                        {
                            c.Connect(3000);
                            var w = new StreamWriter(c, new UTF8Encoding(false));
                            foreach (var f in files) w.WriteLine(f);
                            w.Flush();
                        }
                        return null;
                    }
                    catch (TimeoutException) { return files; } // collector already gone: handle our own files
                    catch (IOException) { return files; }
                }

                var all = new List<string>(files);
                // Ask Explorer what is selected: once every selected item has arrived there is nothing to wait for
                // (one item: no wait at all). Without an answer, wait until no process has called for 800 ms.
                var expected = ExplorerSelection(files);
                bool Complete() => expected != null && expected.IsSubsetOf(all.Select(f => f.TrimEnd('\\')));
                while (!Complete())
                {
                    using (var s = new NamedPipeServerStream(name, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous))
                    {
                        var ar = s.BeginWaitForConnection(null, null);
                        if (!ar.AsyncWaitHandle.WaitOne(800)) break; // quiet for a moment: selection complete
                        s.EndWaitForConnection(ar);
                        using (var r = new StreamReader(s, Encoding.UTF8))
                            for (string l; (l = r.ReadLine()) != null;)
                                if (l.Length > 0) all.Add(l);
                    }
                }
                mutex.ReleaseMutex();
                return all.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            }
        }

        /// <summary>
        /// The items selected in the Explorer window showing these files' folder, if that selection includes them.
        /// null when no such window is found (desktop, file dialogs, command line): the caller then falls back to waiting.
        /// </summary>
        static HashSet<string> ExplorerSelection(string[] files)
        {
            try
            {
                var mine = files.Select(f => f.TrimEnd('\\')).ToList();
                var dir = Path.GetDirectoryName(mine[0]);
                if (dir == null) return null;
                dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("Shell.Application"));
                foreach (dynamic window in shell.Windows())
                {
                    try
                    {
                        string folder = window.Document.Folder.Self.Path;
                        if (!string.Equals(folder.TrimEnd('\\'), dir.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)) continue;
                        var selected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        foreach (dynamic item in window.Document.SelectedItems()) selected.Add(((string)item.Path).TrimEnd('\\'));
                        if (mine.All(selected.Contains)) return selected;
                    }
                    catch { } // not a folder view (e.g. Control Panel): try the next window
                }
            }
            catch { }
            return null;
        }
    }
}
