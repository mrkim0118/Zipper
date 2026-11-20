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
        public string Output, Format = "zip", Password, Volume;
        public int Level = 5;
        public bool EncryptNames, Aes = true;
    }

    /// <summary>Extract / compress operations shared by the main window and the Explorer commands.</summary>
    static class Ops
    {
        // shortcut: the password check reads the smallest encrypted file; above this size we skip it to avoid a second full read.
        const long TestLimit = 64L << 20;

        /// <summary>Lists the archive and makes sure we hold the right password (asks the user as needed).</summary>
        public static bool Open(IWin32Window owner, string archive, ref string password, out List<SevenZip.Entry> entries, out string type)
        {
            while (true)
            {
                entries = SevenZip.List(archive, password, out var r, out type);
                if (entries.Count == 0 && r.NeedsPassword)
                {
                    password = Ui.AskPassword(owner, archive, password != null);
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
                password = Ui.AskPassword(owner, archive, true);
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

        /// <summary>Explorer command: open + extract without the main window.</summary>
        public static void ExtractQuick(string archive, Target target)
        {
            string pw = null;
            if (!Open(null, archive, ref pw, out var entries, out _)) return;
            var dest = target == Target.Here ? Path.GetDirectoryName(archive)
                     : target == Target.Folder ? FolderFor(archive) : SmartTarget(archive, entries);
            Extract(null, archive, dest, null, pw, Shell.OpenFolderAfterExtract);
        }

        /// <summary>Extracts (all, or the given entry paths) into dest. Password must already be checked with Open.</summary>
        public static bool Extract(IWin32Window owner, string archive, string dest, IList<string> items, string password,
                                   bool openFolder, string overwrite = null, bool flat = false)
        {
            var args = new List<string> { flat ? "e" : "x", archive, "-o" + dest, "-y", overwrite ?? Shell.OverwriteSwitch };
            string list = null;
            if (items != null && items.Count > 0)
            {
                list = SevenZip.ListFile(items);
                args.Add("-spd"); // names are literal, not wildcards
                args.Add("@" + list);
            }
            try
            {
                var r = ProgressForm.Run(owner, "압축 푸는 중", args, password ?? "");
                if (r.Cancelled) return false;
                if (!r.Ok)
                {
                    Ui.Error(owner, "압축을 푸는 중 오류가 발생했습니다.\n\n" + r.Errors);
                    return false;
                }
                if (openFolder) Process.Start("explorer.exe", "\"" + dest + "\"");
                return true;
            }
            finally { if (list != null) File.Delete(list); }
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

        public static bool Compress(IWin32Window owner, string[] files, CompressOptions o)
        {
            bool existed = File.Exists(o.Output);
            var args = new List<string> { "a", "-t" + o.Format };
            if (o.Format != "tar" && o.Format != "wim") args.Add("-mx=" + o.Level);
            bool pw = !string.IsNullOrEmpty(o.Password) && (o.Format == "zip" || o.Format == "7z");
            if (pw) args.Add("-p");
            if (pw && o.Format == "7z" && o.EncryptNames) args.Add("-mhe=on");
            if (pw && o.Format == "zip" && o.Aes) args.Add("-mem=AES256");
            if (!string.IsNullOrEmpty(o.Volume)) args.Add("-v" + o.Volume);
            var list = SevenZip.ListFile(files.Select(f => f.TrimEnd('\\')));
            args.Add(o.Output);
            args.Add("@" + list);
            try
            {
                var r = ProgressForm.Run(owner, "압축하는 중", args, pw ? o.Password : "");
                if (r.Ok) return true;
                if (!r.Cancelled) Ui.Error(owner, "압축하는 중 오류가 발생했습니다.\n\n" + r.Errors);
                if (!existed) DeletePartial(o.Output);
                return false;
            }
            finally { File.Delete(list); }
        }

        static void DeletePartial(string output)
        {
            try
            {
                var dir = Path.GetDirectoryName(output);
                foreach (var f in Directory.GetFiles(dir, Path.GetFileName(output) + "*"))
                    if (f == output || Regex.IsMatch(f.Substring(output.Length), @"^\.(\d{3}|tmp)$")) File.Delete(f);
            }
            catch { } // best effort: the user is already told the archive failed
        }
    }
}
