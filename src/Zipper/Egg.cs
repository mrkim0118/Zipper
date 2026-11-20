using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using EggDotNet;
using EggDotNet.Exceptions;

namespace Zipper
{
    /// <summary>
    /// ALZ / EGG support (7-Zip cannot read them) through EggDotNet, extract-only.
    /// Answers the same commands SevenZip.Run sends (l -slt, x, e, t) with 7z-style output,
    /// so the rest of the app treats these archives like any other.
    /// </summary>
    static class Egg
    {
        static readonly Regex Ext = new Regex(@"\.(egg|alz)$", RegexOptions.IgnoreCase);
        static readonly Regex Volume = new Regex(@"^(\.vol\d+\.egg|\.alz|\.[a-z]\d\d)$", RegexOptions.IgnoreCase);

        public static bool Handles(IList<string> args) =>
            new[] { "l", "x", "e", "t" }.Contains(args[0]) && args.Skip(1).Any(a => !a.StartsWith("-") && Ext.IsMatch(a));

        public static SevenZip.Result Run(IList<string> args, string password, Action<int, string> progress, Func<bool> cancelled)
        {
            var cmd = args[0];
            var archive = args.Skip(1).First(a => !a.StartsWith("-") && Ext.IsMatch(a));
            var dest = args.FirstOrDefault(a => a.StartsWith("-o"))?.Substring(2);
            var overwrite = args.FirstOrDefault(a => a.StartsWith("-ao")) ?? "-aoa";
            var items = new List<string>();
            foreach (var a in args.Skip(args.IndexOf(archive) + 1).Where(a => !a.StartsWith("-")))
                if (a.StartsWith("@")) items.AddRange(File.ReadAllLines(a.Substring(1), Encoding.UTF8).Where(l => l.Length > 0));
                else items.Add(a);

            var r = new SevenZip.Result();
            var output = new StringBuilder();
            int errors = 0;
            void Error(string msg) { errors++; output.AppendLine("ERROR: " + msg); }
            try
            {
                using (var egg = new EggArchive(File.OpenRead(archive), true, Volumes,
                           (name, o) => { o.Password = password ?? ""; o.Retry = false; }))
                {
                    var entries = egg.Entries.ToList();
                    if (cmd == "l")
                    {
                        output.Append("--\nPath = ").Append(archive).Append("\nType = ").Append(Path.GetExtension(archive).TrimStart('.').ToLowerInvariant())
                              .Append("\n\n----------\n");
                        foreach (var e in entries)
                            output.Append("Path = ").Append(PathOf(e))
                                  .Append("\nFolder = ").Append(IsDir(e) ? "+" : "-")
                                  .Append("\nSize = ").Append(e.UncompressedLength)
                                  .Append("\nPacked Size = ").Append(e.CompressedLength)
                                  .Append("\nModified = ").Append(e.LastWriteTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture))
                                  .Append("\nEncrypted = ").Append(e.IsEncrypted ? "+" : "-")
                                  .Append("\n\n");
                    }
                    else
                    {
                        var picked = entries.Where(e => items.Count == 0 || items.Any(i =>
                            PathOf(e).Equals(i, StringComparison.OrdinalIgnoreCase) ||
                            PathOf(e).StartsWith(i.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase))).ToList();
                        long total = Math.Max(1, picked.Sum(e => e.UncompressedLength)), done = 0;
                        string root = Path.GetFullPath(dest ?? ".").TrimEnd('\\') + "\\";
                        var buf = new byte[1 << 16];
                        foreach (var e in picked)
                        {
                            if (cancelled != null && cancelled()) { r.Cancelled = true; break; }
                            var path = PathOf(e);
                            progress?.Invoke((int)(done * 100 / total), path);
                            if (IsDir(e)) { if (cmd == "x") Directory.CreateDirectory(root + path); continue; }
                            try
                            {
                                if (cmd == "t")
                                {
                                    if (!e.ChecksumValid()) Error("CRC Failed : " + path);
                                    done += e.UncompressedLength;
                                    continue;
                                }
                                var target = Path.GetFullPath(root + (cmd == "e" ? Path.GetFileName(path) : path));
                                if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase)) { Error("Dangerous path : " + path); continue; } // "..\" entries
                                if (File.Exists(target))
                                {
                                    if (overwrite == "-aos") { done += e.UncompressedLength; continue; }
                                    if (overwrite == "-aou") target = Unique(target);
                                }
                                Directory.CreateDirectory(Path.GetDirectoryName(target));
                                using (var src = e.Open()) // throws DecryptFailedException before anything is written
                                {
                                    bool stopped = false;
                                    using (var dst = File.Create(target))
                                        for (int n; (n = src.Read(buf, 0, buf.Length)) > 0;)
                                        {
                                            dst.Write(buf, 0, n);
                                            done += n;
                                            progress?.Invoke((int)(done * 100 / total), path);
                                            if (cancelled != null && cancelled()) { stopped = true; break; }
                                        }
                                    if (stopped) { File.Delete(target); r.Cancelled = true; break; }
                                }
                                try { File.SetLastWriteTime(target, e.LastWriteTime); } catch { }
                            }
                            catch (DecryptFailedException) { Error("Wrong password : " + path); }
                            catch (Exception ex) when (!(ex is OutOfMemoryException)) { Error(path + " : " + ex.Message); }
                        }
                    }
                }
            }
            catch (DecryptFailedException) { Error("Cannot open encrypted archive. Wrong password?"); }
            catch (Exception ex) when (!(ex is OutOfMemoryException)) { Error("Cannot open the file as archive: " + ex.Message); }

            r.ExitCode = r.Cancelled ? 255 : errors > 0 ? 2 : 0;
            r.Output = output.ToString();
            return r;
        }

        static string PathOf(EggArchiveEntry e) => (e.FullName ?? e.Name ?? "").Replace('/', '\\').Trim('\\');

        static bool IsDir(EggArchiveEntry e) =>
            (e.ExternalAttributes & (long)WindowsFileAttributes.Directory) != 0 || (e.FullName ?? "").EndsWith("/") || (e.FullName ?? "").EndsWith("\\");

        /// <summary>Other volumes of a split archive: a.vol1.egg → a.vol2.egg…, a.alz → a.a00, a.a01…</summary>
        static IEnumerable<Stream> Volumes(Stream first)
        {
            var path = ((FileStream)first).Name;
            var stem = Regex.Replace(Path.GetFileName(path), @"(\.vol\d+)?\.(egg|alz)$", "", RegexOptions.IgnoreCase);
            return Directory.GetFiles(Path.GetDirectoryName(path), stem + ".*")
                .Where(f => !f.Equals(path, StringComparison.OrdinalIgnoreCase) && Volume.IsMatch(Path.GetFileName(f).Substring(stem.Length)))
                .Select(f => (Stream)File.OpenRead(f)).ToList();
        }

        static string Unique(string path)
        {
            var dir = Path.GetDirectoryName(path); var name = Path.GetFileNameWithoutExtension(path); var ext = Path.GetExtension(path);
            for (int i = 1; ; i++)
            {
                var p = Path.Combine(dir, $"{name}_{i}{ext}");
                if (!File.Exists(p)) return p;
            }
        }
    }
}
