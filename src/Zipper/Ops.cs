using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows.Forms;

namespace Zipper
{
    class CompressOptions
    {
        public CompressOptions MemberwiseCopy() => (CompressOptions)MemberwiseClone();
        public string Output, Format = "zip", Password, Volume;
        public int Level = 5;
        public bool EncryptNames, Aes = true, ReplaceExisting, RenameExe;
        /// <summary>One archive per selected item, each named after its item and saved next to it.</summary>
        public bool Each;
    }

    /// <summary>Extract / compress operations shared by the main window and the Explorer commands.</summary>
    static class Ops
    {
        // shortcut: the password check reads the smallest encrypted file; above this size we skip it to avoid a second full read.
        const long TestLimit = 64L << 20;

        /// <summary>
        /// Lists the archive and makes sure we hold the right password (asks the user as needed).
        /// A password passed in is a guess (the one that opened the previous archive of a selection): it is tried
        /// first, and if it does not fit, the user is asked without being told it was wrong.
        /// </summary>
        public static bool Open(IWin32Window owner, string archive, ref string password, out List<SevenZip.Entry> entries, out string type)
        {
            bool guess = password != null;
            bool Wrong(string tried) { bool w = tried != null && !guess; guess = false; return w; }
            while (true)
            {
                entries = SevenZip.List(archive, password, out var r, out type);
                if (entries.Count == 0 && r.NeedsPassword)
                {
                    password = Ui.AskPassword(owner, archive, Wrong(password));
                    if (password == null) return false;
                    continue;
                }
                if (entries.Count == 0 && !r.Ok)
                {
                    Ui.Error(owner, $"압축 파일을 열 수 없습니다.\n{archive}\n\n{r.Errors}");
                    return false;
                }
                break;
            }
            // Wrong passwords leave empty files behind, so check one before extracting.
            var enc = entries.Where(e => e.Encrypted && !e.IsDir).OrderBy(e => e.Size).FirstOrDefault();
            while (enc != null && enc.Size <= TestLimit)
            {
                if (password == null)
                {
                    password = Ui.AskPassword(owner, archive, false);
                    if (password == null) return false;
                }
                if (!SevenZip.Run(new[] { "t", archive, "-spd", enc.Path }, password).NeedsPassword) break;
                password = Ui.AskPassword(owner, archive, Wrong(password));
                if (password == null) return false;
            }
            return true;
        }

        /// <summary>Folder name for "extract to new folder": a.tar.gz -> a, b.part1.rar -> b, c.7z.001 -> c, d.vol1.egg -> d.</summary>
        public static string FolderFor(string archive)
        {
            var n = Path.GetFileName(archive);
            n = Regex.Replace(n, @"\.\d{3}$", "");
            n = Regex.Replace(n, @"(\.part\d+|\.vol\d+)?\.[^.]+$", "", RegexOptions.IgnoreCase);
            n = Regex.Replace(n, @"\.tar$", "", RegexOptions.IgnoreCase).Trim();
            return Path.Combine(Path.GetDirectoryName(archive), n.Length > 0 ? n : "extracted");
        }

        /// <summary>"Smart" target: one top-level item -> extract here, otherwise into a new folder.</summary>
        public static string SmartTarget(string archive, List<SevenZip.Entry> entries)
        {
            var roots = entries.Select(e => e.Path.Split('\\', '/')[0]).Distinct(StringComparer.OrdinalIgnoreCase).Count();
            return roots == 1 ? Path.GetDirectoryName(archive) : FolderFor(archive);
        }

        public enum Target { Here, Folder, Smart }

        /// <summary>Explorer command: open + extract without the main window. password carries over between archives.</summary>
        public static void ExtractQuick(string archive, Target target, ref string password)
        {
            if (!Open(null, archive, ref password, out var entries, out _)) return;
            var dest = target == Target.Here ? Path.GetDirectoryName(archive)
                     : target == Target.Folder ? FolderFor(archive) : SmartTarget(archive, entries);
            Extract(null, archive, dest, null, password, Shell.OpenFolderAfterExtract, total: entries.Sum(e => e.Size));
        }

        /// <summary>Explorer command: tests an archive for damage and reports the result.</summary>
        public static void TestQuick(string archive, ref string pw)
        {
            if (!Open(null, archive, ref pw, out _, out _)) return;
            var r = ProgressForm.Run(null, "검사하는 중", new[] { "t", archive }, pw ?? "");
            if (r.Cancelled) return;
            if (r.Ok) MessageBox.Show($"{Path.GetFileName(archive)}\n\n이상 없습니다.", "Zipper", MessageBoxButtons.OK, MessageBoxIcon.Information);
            else Ui.Error(null, $"{Path.GetFileName(archive)}\n\n손상된 파일이 있습니다.\n\n" + r.Errors);
        }

        /// <summary>Extracts (all, or the given entry paths) into dest. Password must already be checked with Open.</summary>
        public static bool Extract(IWin32Window owner, string archive, string dest, IList<string> items, string password,
                                   bool openFolder, string overwrite = null, bool flat = false, long total = 0)
        {
            // -snz: files from a downloaded archive keep the "came from the internet" mark (SmartScreen, Office Protected View).
            var args = new List<string> { flat ? "e" : "x", archive, "-o" + dest, "-y", "-snz", overwrite ?? Shell.OverwriteSwitch };
            string list = null;
            if (items != null && items.Count > 0)
            {
                list = SevenZip.ListFile(items);
                args.Add("-spd"); // names are literal, not wildcards
                args.Add("@" + list);
            }
            try
            {
                var r = ProgressForm.Run(owner, "압축 푸는 중", args, password ?? "", total);
                if (r.Cancelled) return false;
                if (!r.Ok)
                {
                    Ui.Error(owner, "압축을 푸는 중 오류가 발생했습니다.\n\n" + r.Errors);
                    return false;
                }
                NormalizeNames(archive, password, dest, flat);
                if (openFolder) Process.Start("explorer.exe", "\"" + dest + "\"");
                return true;
            }
            finally { if (list != null) File.Delete(list); }
        }

        /// <summary>
        /// After extracting: macOS stores Korean names decomposed (NFD: ㅎ+ㅏ+ㄴ), which Windows shows as separate jamo,
        /// so such names are composed (NFC) unless that name is taken. Files stored as .zipper get their .exe back
        /// ("app (2).exe" if app.exe exists), and the restore batch file Zipper added is then no longer needed.
        /// </summary>
        static void NormalizeNames(string archive, string password, string dest, bool flat)
        {
            try
            {
                var names = new HashSet<string>(StringComparer.Ordinal);
                foreach (var e in SevenZip.List(archive, password, out _, out _))
                {
                    var parts = e.Path.Split(new[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length == 0) continue;
                    if (flat) names.Add(parts[parts.Length - 1]);
                    else for (int i = 1; i <= parts.Length; i++) names.Add(string.Join("\\", parts, 0, i));
                }
                // deepest first, so a folder is renamed after everything inside it
                bool restored = false;
                foreach (var rel in names.Where(n => !n.IsNormalized() || IsStandIn(n)).OrderByDescending(n => n.Count(c => c == '\\')))
                {
                    var from = Path.Combine(dest, rel);
                    var to = Path.Combine(Path.GetDirectoryName(from), Path.GetFileName(from).Normalize());
                    if (File.Exists(from) && IsStandIn(to))
                    {
                        var stem = to.Substring(0, to.Length - SevenZip.ExeStandIn.Length);
                        to = stem + ".exe";
                        for (int i = 2; File.Exists(to) || Directory.Exists(to); i++) to = $"{stem} ({i}).exe";
                        restored = true;
                    }
                    if (to == from || File.Exists(to) || Directory.Exists(to)) continue;
                    if (Directory.Exists(from)) Directory.Move(from, to);
                    else if (File.Exists(from)) File.Move(from, to);
                }
                if (restored)
                    foreach (var rel in names.Where(n => Path.GetFileName(n) == SevenZip.RestoreBatName))
                    {
                        var bat = Path.Combine(dest, rel);
                        if (File.Exists(bat) && File.ReadAllText(bat) == SevenZip.RestoreBat) File.Delete(bat); // only our own, unchanged
                    }
            }
            catch { } // best effort: the files are extracted either way
        }

        static bool IsStandIn(string name) => name.EndsWith(SevenZip.ExeStandIn, StringComparison.OrdinalIgnoreCase);

        /// <summary>Where an extracted entry ended up: its own name, or the NFC / .exe name NormalizeNames gave it.</summary>
        public static string OnDisk(string path)
        {
            var nfc = string.Join("\\", path.Split('\\').Select(p => p.Normalize()));
            foreach (var p in new[] { path, nfc })
            {
                if (File.Exists(p) || Directory.Exists(p)) return p;
                if (IsStandIn(p) && File.Exists(p.Substring(0, p.Length - SevenZip.ExeStandIn.Length) + ".exe"))
                    return p.Substring(0, p.Length - SevenZip.ExeStandIn.Length) + ".exe";
            }
            return path;
        }

        /// <summary>Default archive name next to the inputs: one item -> its name, several -> the parent folder's name.</summary>
        public static string DefaultOutput(string[] files, string ext)
        {
            var first = files[0].TrimEnd('\\');
            var dir = Path.GetDirectoryName(first) ?? first;
            var name = files.Length == 1
                ? (Directory.Exists(first) ? Path.GetFileName(first) : Path.GetFileNameWithoutExtension(first))
                : Path.GetFileName(dir.TrimEnd('\\'));
            if (string.IsNullOrEmpty(name)) name = "archive";
            var path = Path.Combine(dir, name + "." + ext);
            for (int i = 2; File.Exists(path) || File.Exists(path + ".001"); i++) path = Path.Combine(dir, $"{name} ({i}).{ext}");
            return path;
        }

        /// <summary>Creates an archive using the selected compression settings and entry names.</summary>
        public static bool Compress(IWin32Window owner, string[] files, CompressOptions o)
        {
            if (o.Each && files.Length > 1)
            {
                bool all = true;
                Program.Batch(files, f =>
                {
                    var one = o.MemberwiseCopy();
                    one.Each = false; one.ReplaceExisting = false;
                    one.Output = DefaultOutput(new[] { f }, o.Format);
                    all &= Compress(owner, new[] { f }, one);
                });
                return all;
            }
            if (o.Format.StartsWith("tar.")) return CompressTarred(owner, files, o);
            var args = new List<string> { "a", "-t" + o.Format };
            if (o.Format == "gz" || o.Format == "bz2" || o.Format == "xz") args.Add("-mx=" + Math.Max(1, o.Level)); // no "store" level
            else if (o.Format != "tar" && o.Format != "wim") args.Add("-mx=" + o.Level);
            bool pw = !string.IsNullOrEmpty(o.Password) && (o.Format == "zip" || o.Format == "7z");
            if (pw) args.Add("-p");
            if (pw && o.Format == "7z" && o.EncryptNames) args.Add("-mhe=on");
            if (pw && o.Format == "zip" && o.Aes) args.Add("-mem=AES256");
            if (!string.IsNullOrEmpty(o.Volume)) args.Add("-v" + o.Volume);
            SevenZip.Result r = ProgressForm.Run(owner, "압축하는 중", (progress, cancelled) =>
                SevenZip.Add(args, o.Output, files, pw ? o.Password : "", progress, cancelled, replaceExisting: o.ReplaceExisting, renameExe: o.RenameExe),
                InputSize(files));
            if (r.Ok) return true;
            if (!r.Cancelled) Ui.Error(owner, "압축하는 중 오류가 발생했습니다.\n\n" + r.Errors);
            return false;
        }

        /// <summary>Total size of the files to compress (for the speed shown while compressing); 0 if it cannot be read.</summary>
        static long InputSize(string[] files)
        {
            try
            {
                return files.Sum(f => Directory.Exists(f)
                    ? new DirectoryInfo(f).EnumerateFiles("*", SearchOption.AllDirectories).Sum(x => x.Length)
                    : new FileInfo(f).Length);
            }
            catch { return 0; } // e.g. a folder we may not read: the archive step reports that
        }

        /// <summary>tar.gz / tar.bz2 / tar.xz: 7-Zip writes these in two steps, a temporary tar and then the compressed file.</summary>
        static bool CompressTarred(IWin32Window owner, string[] files, CompressOptions o)
        {
            var type = new Dictionary<string, string> { ["gz"] = "gzip", ["bz2"] = "bzip2", ["xz"] = "xz" }[o.Format.Substring(4)];
            var stage = Path.Combine(Path.GetTempPath(), "zipper-" + Guid.NewGuid().ToString("N"));
            // Inner name = output name without the compression suffix, so extracting gives "<name>.tar".
            var outName = Path.GetFileName(o.Output);
            var tarName = Regex.Replace(outName, @"\.(gz|bz2|xz)$", "", RegexOptions.IgnoreCase);
            if (!tarName.EndsWith(".tar", StringComparison.OrdinalIgnoreCase)) tarName += ".tar";
            var tar = Path.Combine(stage, tarName);
            SevenZip.Result r = ProgressForm.Run(owner, "압축하는 중", (progress, cancelled) =>
            {
                try
                {
                    Directory.CreateDirectory(stage);
                    var first = SevenZip.Add(new[] { "a", "-ttar" }, tar, files, "", (p, f) => progress(p / 2, f), cancelled, renameExe: o.RenameExe);
                    if (!first.Ok) return first;
                    var args = new List<string> { "a", "-t" + type, "-mx=" + Math.Max(1, o.Level) };
                    if (!string.IsNullOrEmpty(o.Volume)) args.Add("-v" + o.Volume);
                    args.Add(o.Output); args.Add(tar);
                    bool existed = File.Exists(o.Output);
                    if (o.ReplaceExisting) File.Delete(o.Output);
                    var second = SevenZip.Run(args, "", (p, f) => progress(50 + p / 2, outName), cancelled);
                    if (!second.Ok && !existed) SevenZip.DeletePartial(o.Output);
                    return second;
                }
                finally { try { if (Directory.Exists(stage)) Directory.Delete(stage, true); } catch { } }
            }, InputSize(files) * 2); // the data passes twice: into the tar, then through the compressor
            if (r.Ok) return true;
            if (!r.Cancelled) Ui.Error(owner, "압축하는 중 오류가 발생했습니다.\n\n" + r.Errors);
            return false;
        }
    }
}
