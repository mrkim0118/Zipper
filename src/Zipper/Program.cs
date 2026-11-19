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
                    case "/xhere": foreach (var a in rest) Ops.ExtractQuick(a, Ops.Target.Here); return;
                    case "/xfolder": foreach (var a in rest) Ops.ExtractQuick(a, Ops.Target.Folder); return;
                    case "/xsmart": foreach (var a in rest) Ops.ExtractQuick(a, Ops.Target.Smart); return;
                    case "/x":
                        foreach (var a in rest)
                        {
                            string pw = null;
                            if (!Ops.Open(null, a, ref pw, out _, out _)) continue;
                            using (var f = new ExtractForm(a, 0))
                                if (Ui.Show(f, null) == DialogResult.OK)
                                    Ops.Extract(null, a, f.Dest, null, pw, f.OpenFolder, f.OverwriteSwitch);
                        }
                        return;
                    case "/c": case "/czip": case "/c7z":
                        var files = Collect(cmd, rest);
                        if (files == null || files.Length == 0) return; // another instance takes these files
                        if (cmd == "/c")
                        {
                            using (var f = new CompressForm(files, "zip"))
                                if (Ui.Show(f, null) == DialogResult.OK) Ops.Compress(null, files, f.Options);
                        }
                        else
                        {
                            var fmt = cmd == "/czip" ? "zip" : "7z";
                            Ops.Compress(null, files, new CompressOptions { Output = Ops.DefaultOutput(files, fmt), Format = fmt });
                        }
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
                while (true)
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
    }
}
