using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace Zipper
{
    /// <summary>User settings and Explorer integration. Everything lives under HKCU, so no admin rights are needed.</summary>
    static class Shell
    {
        public const string ProgId = "Zipper.Archive";
        const string AppKey = @"Software\Zipper";
        const string Classes = @"Software\Classes\";
        static string ExePath => System.Windows.Forms.Application.ExecutablePath;

        /// <summary>Formats 7-Zip can open that are worth associating (no exe/msi/docx and similar).</summary>
        public static readonly string[] Extensions =
        {
            "zip", "7z", "rar", "tar", "gz", "tgz", "bz2", "tbz2", "xz", "txz", "zst", "tzst", "lzma",
            "cab", "iso", "wim", "arj", "lzh", "lha", "cpio", "rpm", "deb", "z", "taz", "xar", "zipx", "001", "egg", "alz",
        };

        // ---- settings ----
        static object Get(string name, object def) => Registry.CurrentUser.OpenSubKey(AppKey)?.GetValue(name) ?? def;
        static void Set(string name, object value) { using (var k = Registry.CurrentUser.CreateSubKey(AppKey)) k.SetValue(name, value); }

        /// <summary>0 = overwrite, 1 = skip, 2 = rename new file (default: never loses data).</summary>
        public static int OverwriteMode { get => (int)Get("OverwriteMode", 2); set => Set("OverwriteMode", value); }
        public static bool OpenFolderAfterExtract { get => (int)Get("OpenFolder", 1) != 0; set => Set("OpenFolder", value ? 1 : 0); }
        public static string OverwriteSwitch => new[] { "-aoa", "-aos", "-aou" }[Math.Max(0, Math.Min(2, OverwriteMode))];

        // ---- file associations ----
        public static bool IsAssociated(string ext) =>
            Registry.CurrentUser.OpenSubKey(AppKey + @"\Capabilities\FileAssociations")?.GetValue("." + ext) != null;

        public static bool ContextMenuEnabled => Registry.CurrentUser.OpenSubKey(Classes + @"*\shell\Zipper") != null;

        public static void Apply(IEnumerable<string> associate, bool contextMenu)
        {
            var want = new HashSet<string>(associate);
            using (var cls = Registry.CurrentUser.CreateSubKey(Classes + ProgId))
            {
                cls.SetValue("", "Zipper 압축 파일");
                using (var k = cls.CreateSubKey("DefaultIcon")) k.SetValue("", $"\"{ExePath}\",0");
                using (var k = cls.CreateSubKey(@"shell\open\command")) k.SetValue("", $"\"{ExePath}\" \"%1\"");
            }
            using (var cap = Registry.CurrentUser.CreateSubKey(AppKey + @"\Capabilities"))
            {
                cap.SetValue("ApplicationName", "Zipper");
                cap.SetValue("ApplicationDescription", "Zipper 압축 프로그램");
            }
            using (var reg = Registry.CurrentUser.CreateSubKey(@"Software\RegisteredApplications"))
                reg.SetValue("Zipper", AppKey + @"\Capabilities");

            foreach (var ext in Extensions)
            {
                if (want.Contains(ext)) Associate(ext);
                else if (IsAssociated(ext)) Unassociate(ext);
            }

            if (contextMenu) AddContextMenu(); else RemoveContextMenu();
            SHChangeNotify(0x08000000, 0, IntPtr.Zero, IntPtr.Zero); // SHCNE_ASSOCCHANGED
        }

        static void Associate(string ext)
        {
            using (var k = Registry.CurrentUser.CreateSubKey(Classes + "." + ext))
            {
                var cur = k.GetValue("") as string;
                if (cur != ProgId)
                {
                    if (!string.IsNullOrEmpty(cur)) k.SetValue("Zipper.Backup", cur);
                    k.SetValue("", ProgId);
                }
                using (var o = k.CreateSubKey("OpenWithProgids")) o.SetValue(ProgId, new byte[0], RegistryValueKind.None);
            }
            using (var fa = Registry.CurrentUser.CreateSubKey(AppKey + @"\Capabilities\FileAssociations")) fa.SetValue("." + ext, ProgId);
        }

        static void Unassociate(string ext)
        {
            using (var k = Registry.CurrentUser.OpenSubKey(Classes + "." + ext, true))
            {
                if (k != null)
                {
                    if (k.GetValue("") as string == ProgId)
                    {
                        if (k.GetValue("Zipper.Backup") is string old) k.SetValue("", old); else k.DeleteValue("", false);
                    }
                    k.DeleteValue("Zipper.Backup", false);
                    using (var o = k.OpenSubKey("OpenWithProgids", true))
                    {
                        o?.DeleteValue(ProgId, false);
                        if (o != null && o.ValueCount == 0 && o.SubKeyCount == 0) k.DeleteSubKey("OpenWithProgids", false);
                    }
                }
            }
            Registry.CurrentUser.OpenSubKey(AppKey + @"\Capabilities\FileAssociations", true)?.DeleteValue("." + ext, false);
        }

        // ---- Explorer context menu (classic menu; on Windows 11 under "Show more options") ----
        static void Verb(string parent, string key, string text, string args, bool multi = false)
        {
            using (var k = Registry.CurrentUser.CreateSubKey($@"{Classes}{parent}\shell\{key}"))
            {
                k.SetValue("MUIVerb", text);
                if (multi) k.SetValue("MultiSelectModel", "Player"); // keep the verb visible for >15 selected items
                using (var c = k.CreateSubKey("command")) c.SetValue("", $"\"{ExePath}\" {args} \"%1\"");
            }
        }

        static void Menu(string parent, Action<string> items)
        {
            using (var k = Registry.CurrentUser.CreateSubKey($@"{Classes}{parent}\shell\Zipper"))
            {
                k.SetValue("MUIVerb", "Zipper");
                k.SetValue("Icon", $"\"{ExePath}\",0");
                k.SetValue("SubCommands", "");
            }
            items($@"{parent}\shell\Zipper");
        }

        static void CompressVerbs(string m)
        {
            Verb(m, "5compress", "압축하기...", "/c", true);
            Verb(m, "6zip", "ZIP으로 바로 압축", "/czip", true);
            Verb(m, "7sevenzip", "7Z로 바로 압축", "/c7z", true);
        }

        static void AddContextMenu()
        {
            Menu("*", CompressVerbs);
            Menu("Directory", CompressVerbs);
            // Same verb name "Zipper": Explorer uses the more specific SystemFileAssociations entry on archives.
            foreach (var ext in Extensions)
                Menu($@"SystemFileAssociations\.{ext}", m =>
                {
                    Verb(m, "0xsmart", "알아서 풀기", "/xsmart");
                    Verb(m, "1xhere", "여기에 풀기", "/xhere");
                    Verb(m, "2xfolder", "새 폴더에 풀기", "/xfolder");
                    Verb(m, "3x", "다른 위치에 풀기...", "/x");
                    Verb(m, "4open", "Zipper로 열기", "");
                    CompressVerbs(m);
                });
        }

        static void RemoveContextMenu()
        {
            Registry.CurrentUser.DeleteSubKeyTree(Classes + @"*\shell\Zipper", false);
            Registry.CurrentUser.DeleteSubKeyTree(Classes + @"Directory\shell\Zipper", false);
            foreach (var ext in Extensions)
                Registry.CurrentUser.DeleteSubKeyTree($@"{Classes}SystemFileAssociations\.{ext}\shell\Zipper", false);
        }

        /// <summary>Removes everything Zipper wrote, settings included (used by the uninstaller).</summary>
        public static void Unregister()
        {
            Apply(Enumerable.Empty<string>(), false);
            Registry.CurrentUser.DeleteSubKeyTree(Classes + ProgId, false);
            Registry.CurrentUser.OpenSubKey(@"Software\RegisteredApplications", true)?.DeleteValue("Zipper", false);
            Registry.CurrentUser.DeleteSubKeyTree(AppKey, false);
            SHChangeNotify(0x08000000, 0, IntPtr.Zero, IntPtr.Zero);
        }

        [DllImport("shell32.dll")]
        static extern void SHChangeNotify(int eventId, int flags, IntPtr item1, IntPtr item2);
    }
}
