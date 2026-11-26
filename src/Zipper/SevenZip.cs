using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace Zipper
{
    /// <summary>Thin wrapper around the bundled 7z.exe (7-Zip console).</summary>
    static class SevenZip
    {
        public static string Exe => Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "7z.exe");

        public class Result
        {
            public const int FatalError = 2;
            public int ExitCode;
            public string Output = "";
            public bool Cancelled;
            public bool Ok => !Cancelled && (ExitCode == 0 || ExitCode == 1); // 1 = warnings only
            // 7z prints the prompt even when stdin answers it, so a prompt only counts if the run failed
            public bool NeedsPassword => Output.Contains("Wrong password") || (!Ok && Output.Contains("Enter password"));
            public string Errors => string.Join(Environment.NewLine, Output.Split('\n')
                .Select(l => l.Trim()).Where(l => l.StartsWith("ERROR") || l.StartsWith("WARNING") || l.Contains("Error"))
                .Distinct().Take(15));
        }

        public class Entry
        {
            public string Path;
            public bool IsDir, Encrypted;
            public long Size, Packed;
            public DateTime? Modified;
        }

        static string Quote(string a) => a.Length > 0 && a.IndexOfAny(new[] { ' ', '\t' }) < 0 ? a : "\"" + a + "\"";

        static readonly Regex ProgressRx = new Regex(@"^\s*(\d+)%(?:\s+\d+)?(?:\s+[-+U=R.]\s+(.+))?", RegexOptions.Compiled);

        /// <summary>
        /// Runs 7z. args[0] is the command. The password goes through stdin (twice, for 'a' which asks to verify),
        /// so it never shows up in the process command line and may contain any character.
        /// </summary>
        public static Result Run(IList<string> args, string password = null, Action<int, string> progress = null,
                                 Func<bool> cancelled = null, string workDir = null)
        {
            if (Egg.Handles(args)) return Egg.Run(args, password, progress, cancelled);
            var all = new List<string> { args[0], "-sccUTF-8", "-scsUTF-8", "-bsp1", "-bso1", "-bse1" };
            all.AddRange(args.Skip(1));
            var psi = new ProcessStartInfo(Exe, string.Join(" ", all.Select(Quote)))
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
                WorkingDirectory = workDir ?? Environment.CurrentDirectory,
            };
            var r = new Result();
            var output = new StringBuilder();
            using (var p = Process.Start(psi))
            {
                var stdin = new StreamWriter(p.StandardInput.BaseStream, new UTF8Encoding(false));
                if (password != null) stdin.Write(password + "\r\n" + password + "\r\n");
                stdin.Close();

                var reader = new Thread(() =>
                {
                    var buf = new char[4096]; var seg = new StringBuilder(); int n;
                    char prev = '\0'; bool crFlushed = false;
                    while ((n = p.StandardOutput.Read(buf, 0, buf.Length)) > 0)
                        for (int i = 0; i < n; i++)
                        {
                            char c = buf[i];
                            if (c == '\b' || c == '\r' || c == '\n')
                            {
                                // Blank lines matter (they separate "l -slt" entries), but "\r\n" is one line end.
                                bool flush = c == '\n' ? !(prev == '\r' && crFlushed) : seg.Length > 0;
                                if (c == '\r') crFlushed = flush;
                                prev = c;
                                if (flush)
                                {
                                    var m = ProgressRx.Match(seg.ToString());
                                    if (m.Success) progress?.Invoke(int.Parse(m.Groups[1].Value), m.Groups[2].Value.Trim());
                                    else lock (output) output.AppendLine(seg.ToString());
                                    seg.Clear();
                                }
                            }
                            else { seg.Append(c); prev = c; }
                        }
                    if (seg.Length > 0) lock (output) output.AppendLine(seg.ToString());
                }) { IsBackground = true };
                reader.Start();
                var err = p.StandardError.ReadToEndAsync();

                while (!p.WaitForExit(100))
                    if (cancelled != null && cancelled())
                    {
                        r.Cancelled = true;
                        try { p.Kill(); } catch { }
                    }
                p.WaitForExit();
                reader.Join(2000);
                r.ExitCode = p.ExitCode;
                lock (output) r.Output = output + err.Result;
            }
            if (r.ExitCode == 255) r.Cancelled = r.Cancelled || !r.NeedsPassword;
            return r;
        }

        // A hard link lets 7-Zip read a large file under its archive name without copying its contents.
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        static extern bool CreateHardLink(string newFile, string existingFile, IntPtr securityAttributes);

        /// <summary>Creates or updates an archive. With renameExe, file entries ending in .exe are stored as .ex.</summary>
        public static Result Add(IList<string> options, string output, string[] files, string password,
                                 Action<int, string> progress, Func<bool> cancelled, ISet<string> existing = null,
                                 bool replaceExisting = false, bool renameExe = false)
        {
            string stage = Path.Combine(Path.GetTempPath(), "zipper-" + Guid.NewGuid().ToString("N"));
            string list = null;
            List<string> readOnlyCopies = new List<string>();
            try
            {
                List<string> inputs = new List<string>();
                HashSet<string> rootNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                output = Path.GetFullPath(output);
                List<string> sources = new List<string>();
                foreach (string file in files)
                {
                    string path = Path.GetFullPath(file);
                    if (Directory.Exists(path) && new DirectoryInfo(path).Parent == null)
                        sources.AddRange(Directory.GetFileSystemEntries(path).Where(child => !string.Equals(child, output, StringComparison.OrdinalIgnoreCase)));
                    else
                        sources.Add(path.TrimEnd(Path.DirectorySeparatorChar));
                }
                foreach (string source in sources)
                {
                    List<(FileSystemInfo Source, string Name)> entries = new List<(FileSystemInfo, string)>();
                    bool renamed = false;

                    // Collect first so a temporary directory inside a selected folder cannot be archived.
                    void Collect(FileSystemInfo item, string parent)
                    {
                        if (cancelled()) throw new OperationCanceledException();
                        if (string.Equals(item.FullName, stage, StringComparison.OrdinalIgnoreCase)) return;
                        if (string.Equals(item.FullName, output, StringComparison.OrdinalIgnoreCase))
                        {
                            if (parent.Length == 0) throw new IOException("압축 대상과 저장할 압축 파일의 경로가 같습니다.");
                            return;
                        }
                        string name = item.Name;
                        DirectoryInfo directory = item as DirectoryInfo;
                        if (renameExe && directory == null && name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                        {
                            // app.exe -> app.ex (keeps the original case). If app.ex is already taken, by a file next
                            // to it or by an entry of the archive being updated, use "app (2).ex" and so on.
                            string ex = name.Substring(name.Length - 4, 3), stem = name.Substring(0, name.Length - 4);
                            string dir = Path.GetDirectoryName(item.FullName);
                            bool Taken(string n) => File.Exists(Path.Combine(dir, n)) || Directory.Exists(Path.Combine(dir, n))
                                                    || (existing != null && existing.Contains(Path.Combine(parent, n)));
                            name = stem + ex;
                            for (int i = 2; Taken(name); i++) name = $"{stem} ({i}){ex}";
                            renamed = true;
                        }
                        string path = Path.Combine(parent, name);
                        entries.Add((item, path));
                        if (directory != null)
                            foreach (FileSystemInfo child in directory.EnumerateFileSystemInfos()) Collect(child, path);
                    }

                    if (Directory.Exists(source)) Collect(new DirectoryInfo(source), "");
                    else Collect(new FileInfo(source), "");
                    string rootName = entries[0].Name;
                    if (!rootNames.Add(rootName)) throw new IOException("압축할 이름이 겹칩니다: " + rootName);
                    bool containsStage = stage.StartsWith(source + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
                    if (!renamed && !containsStage)
                    {
                        inputs.Add(source);
                        continue;
                    }

                    HashSet<string> names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    Directory.CreateDirectory(stage);
                    foreach ((FileSystemInfo item, string name) in entries)
                    {
                        if (cancelled()) throw new OperationCanceledException();
                        if (!names.Add(name)) throw new IOException("압축할 이름이 겹칩니다: " + name);
                        string dest = Path.Combine(stage, name);
                        if (item is DirectoryInfo)
                        {
                            Directory.CreateDirectory(dest);
                            continue;
                        }
                        // A read-only hard link cannot be removed without changing the source attributes.
                        FileAttributes attributes = item.Attributes;
                        bool linked = false;
                        if ((attributes & (FileAttributes.ReadOnly | FileAttributes.ReparsePoint)) == 0)
                            linked = CreateHardLink(dest, item.FullName, IntPtr.Zero);
                        if (!linked)
                        {
                            if ((attributes & FileAttributes.ReadOnly) != 0) readOnlyCopies.Add(dest);
                            File.Copy(item.FullName, dest);
                        }
                    }
                    for (int i = entries.Count - 1; i >= 0; i--)
                        if (entries[i].Source is DirectoryInfo)
                            Directory.SetLastWriteTimeUtc(Path.Combine(stage, entries[i].Name), entries[i].Source.LastWriteTimeUtc);
                    inputs.Add(Path.Combine(stage, rootName));
                }
                if (cancelled()) throw new OperationCanceledException();
                list = ListFile(inputs);
                List<string> args = new List<string>(options) { output, "@" + list };
                bool existed = File.Exists(output);
                if (replaceExisting) File.Delete(output);
                Result result = Run(args, password, progress, cancelled);
                if (!result.Ok && !existed) DeletePartial(output);
                return result;
            }
            catch (OperationCanceledException) { return new Result { Cancelled = true }; }
            finally
            {
                if (list != null) File.Delete(list);
                foreach (string copy in readOnlyCopies)
                    if (File.Exists(copy)) File.SetAttributes(copy, FileAttributes.Normal);
                if (Directory.Exists(stage)) Directory.Delete(stage, true);
            }
        }

        /// <summary>Removes incomplete output after a failed compression command.</summary>
        public static void DeletePartial(string output)
        {
            try
            {
                var dir = Path.GetDirectoryName(output);
                foreach (var f in Directory.GetFiles(dir, Path.GetFileName(output) + "*"))
                    if (f == output || Regex.IsMatch(f.Substring(output.Length), @"^\.(\d{3}|tmp)$")) File.Delete(f);
            }
            catch { } // best effort: the user is already told the archive failed
        }

        /// <summary>Lists an archive (7z l -slt). type receives the archive format (zip, 7z, Rar5...).</summary>
        public static List<Entry> List(string archive, string password, out Result result, out string type)
        {
            result = Run(new[] { "l", "-slt", archive }, password);
            type = "";
            var list = new List<Entry>();
            var text = result.Output.Replace("\r", "");
            int sep = text.IndexOf("\n----------\n", StringComparison.Ordinal);
            if (sep < 0) return list;
            var t = Regex.Matches(text.Substring(0, sep), @"^Type = (.+)$", RegexOptions.Multiline);
            if (t.Count > 0) type = t[t.Count - 1].Groups[1].Value.Trim();

            foreach (var block in text.Substring(sep + 12).Split(new[] { "\n\n" }, StringSplitOptions.RemoveEmptyEntries))
            {
                var kv = new Dictionary<string, string>();
                foreach (var line in block.Split('\n'))
                {
                    int i = line.IndexOf(" = ", StringComparison.Ordinal);
                    if (i > 0) kv[line.Substring(0, i)] = line.Substring(i + 3);
                }
                if (!kv.TryGetValue("Path", out var path) || path.Length == 0) continue;
                kv.TryGetValue("Attributes", out var attr);
                kv.TryGetValue("Folder", out var folder);
                list.Add(new Entry
                {
                    Path = path,
                    IsDir = folder == "+" || (attr != null && attr.StartsWith("D")),
                    Encrypted = kv.TryGetValue("Encrypted", out var enc) && enc == "+",
                    Size = kv.TryGetValue("Size", out var s) && long.TryParse(s, out var sv) ? sv : 0,
                    Packed = kv.TryGetValue("Packed Size", out var ps) && long.TryParse(ps, out var pv) ? pv : 0,
                    Modified = kv.TryGetValue("Modified", out var md) && DateTime.TryParse(md.Split('.')[0], out var dv) ? dv : (DateTime?)null,
                });
            }
            return list;
        }

        /// <summary>Writes names to a UTF-8 list file for 7z's @listfile syntax (avoids command-line length limits).</summary>
        public static string ListFile(IEnumerable<string> names)
        {
            var f = Path.Combine(Path.GetTempPath(), "zipper-" + Guid.NewGuid().ToString("N") + ".txt");
            File.WriteAllLines(f, names, new UTF8Encoding(false));
            return f;
        }
    }
}
