using System;
using System.Collections.Generic;
using System.IO;
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
        /// <summary>Stores .exe files as .ex inside new archives (mail services often block .exe attachments).</summary>
        public static bool RenameExe { get => (int)Get("RenameExe", 1) != 0; set => Set("RenameExe", value ? 1 : 0); }
        /// <summary>Recently opened archives, newest first (at most 10; missing files are dropped when read).</summary>
        public static string[] Recent
        {
            get => ((Get("Recent", new string[0]) as string[]) ?? new string[0]).Where(File.Exists).ToArray();
        }

        public static void AddRecent(string path)
        {
            var list = new[] { path }.Concat(Recent.Where(p => !p.Equals(path, StringComparison.OrdinalIgnoreCase))).Take(10).ToArray();
            using (var k = Registry.CurrentUser.CreateSubKey(AppKey)) k.SetValue("Recent", list, RegistryValueKind.MultiString);
        }

        public static string OverwriteSwitch => new[] { "-aoa", "-aos", "-aou" }[Math.Max(0, Math.Min(2, OverwriteMode))];

        // ---- file associations ----
        public static bool IsAssociated(string ext) =>
            Registry.CurrentUser.OpenSubKey(AppKey + @"\Capabilities\FileAssociations")?.GetValue("." + ext) != null;

        // Windows 11 opens a short menu first and keeps classic verbs (ours) under "Show more options". An empty
        // InprocServer32 for this CLSID makes Explorer show the full classic menu right away (per user; Explorer restart).
        const string Win11MenuKey = @"Software\Classes\CLSID\{86ca1aa0-34aa-4e8b-a509-50c905bae2a2}";
        public static bool IsWindows11 => Environment.OSVersion.Version.Build >= 22000;
        public static bool ClassicMenu
        {
            get => Registry.CurrentUser.OpenSubKey(Win11MenuKey + @"\InprocServer32") != null;
            set
            {
                if (value) using (var k = Registry.CurrentUser.CreateSubKey(Win11MenuKey + @"\InprocServer32")) k.SetValue("", "");
                else Registry.CurrentUser.DeleteSubKeyTree(Win11MenuKey, false);
            }
        }

        public static void RestartExplorer()
        {
            foreach (var p in System.Diagnostics.Process.GetProcessesByName("explorer")) try { p.Kill(); p.WaitForExit(5000); } catch { }
            System.Threading.Thread.Sleep(1500); // Windows usually restarts the shell by itself
            if (System.Diagnostics.Process.GetProcessesByName("explorer").Length == 0) System.Diagnostics.Process.Start("explorer.exe");
        }

        // Verbs sit directly in the context menu (no cascading "Zipper" submenu). Old versions used that submenu key.
        const string Prefix = "Zipper.";
        const string OldMenuKey = "Zipper";

        public static bool ContextMenuEnabled => Registry.CurrentUser.OpenSubKey(Classes + @"*\shell\" + Prefix + "1compress") != null;

        /// <summary>
        /// Adds the Explorer menu the first time Zipper runs (also without the installer), and rewrites it when the
        /// exe has moved or an older menu layout is found. Turning the menu off in Settings is remembered.
        /// </summary>
        public static void EnsureContextMenu()
        {
            try
            {
                bool first = (int)Get("MenuInit", 0) == 0;
                bool old = Registry.CurrentUser.OpenSubKey(Classes + @"*\shell\" + OldMenuKey) != null;
                var cmd = Registry.CurrentUser.OpenSubKey(Classes + @"*\shell\" + Prefix + @"1compress\command")?.GetValue("") as string;
                bool moved = ContextMenuEnabled && (cmd == null || !cmd.StartsWith($"\"{ExePath}\"", StringComparison.OrdinalIgnoreCase));
                bool iconsGone = ContextMenuEnabled && !File.Exists(IconFile(true));
                if (!first && !old && !moved && !iconsGone) return;
                RemoveContextMenu(); // every case that gets here wants the menu (back) in the current layout
                AddContextMenu();
                Set("MenuInit", 1);
                SHChangeNotify(0x08000000, 0, IntPtr.Zero, IntPtr.Zero);
            }
            catch { } // the menu is a convenience; never block startup
        }

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

            RemoveContextMenu(); // drops verbs of older versions too
            if (contextMenu) AddContextMenu();
            Set("MenuInit", 1);
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
        // Icons for the menu, drawn like the toolbar icons and saved per user (the install folder may be read-only).
        static string IconDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Zipper");
        static string IconFile(bool extract) => Path.Combine(IconDir, extract ? "menu-extract.ico" : "menu-compress.ico");

        static void WriteIcons()
        {
            Directory.CreateDirectory(IconDir);
            foreach (var extract in new[] { true, false })
                using (var f = File.Create(IconFile(extract)))
                    Ui.WriteIco(f, new[] { 16, 20, 24, 32, 48 }.Select(n => Ui.ArchiveIcon(n, extract)).ToArray());
        }

        const int SeparatorBefore = 0x20, SeparatorAfter = 0x40; // ECF_SEPARATORBEFORE / ECF_SEPARATORAFTER

        static void Verb(string parent, string key, string text, string args, bool extract, int flags = 0, bool multi = true)
        {
            using (var k = Registry.CurrentUser.CreateSubKey($@"{Classes}{parent}\shell\{Prefix}{key}"))
            {
                k.SetValue("MUIVerb", text);
                k.SetValue("Icon", $"\"{IconFile(extract)}\"");
                if (multi) k.SetValue("MultiSelectModel", "Player"); // keep the verb visible for >15 selected items
                if (flags != 0) k.SetValue("CommandFlags", flags, RegistryValueKind.DWord);
                using (var c = k.CreateSubKey("command")) c.SetValue("", $"\"{ExePath}\" {args} \"%1\"".Replace("  ", " "));
            }
        }

        static void AddContextMenu()
        {
            WriteIcons();
            // Every file and folder (archives too, through "*"): compress.
            foreach (var parent in new[] { "*", "Directory" })
            {
                Verb(parent, "1compress", "Zipper로 압축하기...", "/c", false, SeparatorBefore);
                Verb(parent, "2zip", "ZIP으로 바로 압축", "/czip", false);
                Verb(parent, "3sevenzip", "7Z로 바로 압축", "/c7z", false, SeparatorAfter);
            }
            // Archives only: extract, open and test.
            foreach (var ext in Extensions)
            {
                var a = $@"SystemFileAssociations\.{ext}";
                Verb(a, "0a-xsmart", "알아서 풀기", "/xsmart", true, SeparatorBefore);
                Verb(a, "0b-xhere", "여기에 풀기", "/xhere", true);
                Verb(a, "0c-xfolder", "각각 새 폴더에 풀기", "/xfolder", true);
                Verb(a, "0d-x", "다른 위치에 풀기...", "/x", true);
                Verb(a, "0e-open", "Zipper로 열기", "", true, multi: false);
                Verb(a, "0f-test", "압축 파일 테스트", "/t", true, SeparatorAfter);
            }
        }

        static void RemoveContextMenu()
        {
            var parents = new List<string> { "*", "Directory" };
            parents.AddRange(Extensions.Select(e => $@"SystemFileAssociations\.{e}"));
            foreach (var parent in parents)
                using (var shell = Registry.CurrentUser.OpenSubKey($@"{Classes}{parent}\shell", true))
                {
                    if (shell == null) continue;
                    foreach (var name in shell.GetSubKeyNames())
                        if (name == OldMenuKey || name.StartsWith(Prefix)) shell.DeleteSubKeyTree(name, false);
                }
        }

        /// <summary>Removes everything Zipper wrote, settings included (used by the uninstaller).</summary>
        public static void Unregister()
        {
            Apply(Enumerable.Empty<string>(), false);
            Registry.CurrentUser.DeleteSubKeyTree(Classes + ProgId, false);
            Registry.CurrentUser.OpenSubKey(@"Software\RegisteredApplications", true)?.DeleteValue("Zipper", false);
            Registry.CurrentUser.DeleteSubKeyTree(AppKey, false);
            try { Directory.Delete(IconDir, true); } catch { } // menu icons
            SHChangeNotify(0x08000000, 0, IntPtr.Zero, IntPtr.Zero);
        }

        [DllImport("shell32.dll")]
        static extern void SHChangeNotify(int eventId, int flags, IntPtr item1, IntPtr item2);
    }
}
