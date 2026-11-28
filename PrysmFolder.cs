// AppFolder - a phone-style app folder for the Windows desktop.
// Build with build.bat (uses the C# compiler that ships with Windows; nothing to install).
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

class Item
{
    public string Path;
    public string Label;
    public Image Icon;
    public string Id;   // the file/folder's ID on its drive, to find it again if it's moved (FileTracker)
}

// Finds a file or folder again after it has been moved or renamed. Every file and folder on an
// NTFS drive has an ID that stays the same when it moves within that drive; we remember
// "drive serial + ID" and ask Windows for its current path when the old one is gone.
static class FileTracker
{
    [StructLayout(LayoutKind.Sequential)]
    struct FileInfo
    {
        public uint Attributes, CreatedLow, CreatedHigh, AccessedLow, AccessedHigh, WrittenLow, WrittenHigh;
        public uint VolumeSerial, SizeHigh, SizeLow, Links, IndexHigh, IndexLow;
    }
    [StructLayout(LayoutKind.Explicit, Size = 24)]
    struct FileIdDescriptor { [FieldOffset(0)] public uint Size; [FieldOffset(4)] public int Type; [FieldOffset(8)] public long FileId; }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern Microsoft.Win32.SafeHandles.SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr sa, uint disposition, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool GetFileInformationByHandle(Microsoft.Win32.SafeHandles.SafeFileHandle h, out FileInfo info);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern Microsoft.Win32.SafeHandles.SafeFileHandle OpenFileById(Microsoft.Win32.SafeHandles.SafeFileHandle volumeHint, ref FileIdDescriptor id, uint access, uint share, IntPtr sa, uint flags);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern uint GetFinalPathNameByHandleW(Microsoft.Win32.SafeHandles.SafeFileHandle h, StringBuilder path, uint size, uint flags);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool GetVolumeInformationW(string root, StringBuilder name, int nameSize, out uint serial, out uint maxLen, out uint fsFlags, StringBuilder fsName, int fsNameSize);

    const uint ShareAll = 0x7, OpenExisting = 3, BackupSemantics = 0x02000000;

    static Microsoft.Win32.SafeHandles.SafeFileHandle Open(string path)
    {
        return CreateFileW(path, 0, ShareAll, IntPtr.Zero, OpenExisting, BackupSemantics, IntPtr.Zero);   // works for folders too
    }

    // "serial-id" for an existing file or folder, or null (web addresses, other kinds of drive)
    public static string IdOf(string path)
    {
        if (string.IsNullOrEmpty(path) || Native.IsUrl(path)) return null;
        try
        {
            using (var h = Open(path))
            {
                FileInfo fi;
                if (h.IsInvalid || !GetFileInformationByHandle(h, out fi)) return null;
                return fi.VolumeSerial.ToString("x8") + "-" + (((ulong)fi.IndexHigh << 32) | fi.IndexLow).ToString("x16");
            }
        }
        catch { return null; }
    }

    // Fallback (no ID saved yet, or moved to another drive): look for the same name in the usual
    // places, a couple of folders deep. Only used when there's exactly one match.
    public static string FindByName(string oldPath)
    {
        string name;
        try { name = Path.GetFileName(oldPath.TrimEnd('\\')); } catch { return null; }
        if (string.IsNullOrEmpty(name)) return null;
        var roots = new List<string>();
        foreach (var sf in new[] { Environment.SpecialFolder.DesktopDirectory, Environment.SpecialFolder.MyDocuments,
                                   Environment.SpecialFolder.MyPictures, Environment.SpecialFolder.MyMusic,
                                   Environment.SpecialFolder.MyVideos, Environment.SpecialFolder.UserProfile })
            roots.Add(Environment.GetFolderPath(sf));
        roots.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads"));
        var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in roots) Search(r, name, 2, found, seen);
        found.Remove(oldPath);
        return found.Count == 1 ? new List<string>(found)[0] : null;
    }

    static void Search(string dir, string name, int depth, HashSet<string> found, HashSet<string> seen)
    {
        if (string.IsNullOrEmpty(dir) || found.Count > 1 || !seen.Add(dir)) return;
        try
        {
            string here = Path.Combine(dir, name);
            if (File.Exists(here) || Directory.Exists(here)) found.Add(here);
            if (depth <= 0) return;
            foreach (var sub in Directory.GetDirectories(dir))
            {
                var attrs = File.GetAttributes(sub);
                if ((attrs & (FileAttributes.Hidden | FileAttributes.System | FileAttributes.ReparsePoint)) != 0) continue;
                if (sub.EndsWith(@"\AppData", StringComparison.OrdinalIgnoreCase)) continue;
                Search(sub, name, depth - 1, found, seen);
            }
        }
        catch { }
    }

    // Where that file or folder is now, or null if it's gone (deleted, in the Recycle Bin, or on
    // a drive that isn't connected).
    public static string Find(string id)
    {
        if (string.IsNullOrEmpty(id)) return null;
        try
        {
            var parts = id.Split('-');
            uint serial = Convert.ToUInt32(parts[0], 16);
            long fileId = (long)Convert.ToUInt64(parts[1], 16);
            foreach (var drive in DriveInfo.GetDrives())
            {
                uint s, maxLen, fsFlags;
                if (!drive.IsReady || !GetVolumeInformationW(drive.RootDirectory.FullName, null, 0, out s, out maxLen, out fsFlags, null, 0) || s != serial) continue;
                using (var root = Open(drive.RootDirectory.FullName))
                {
                    if (root.IsInvalid) return null;
                    var desc = new FileIdDescriptor { Size = 24, Type = 0, FileId = fileId };
                    using (var h = OpenFileById(root, ref desc, 0, ShareAll, IntPtr.Zero, BackupSemantics))
                    {
                        if (h.IsInvalid) return null;
                        var sb = new StringBuilder(1024);
                        if (GetFinalPathNameByHandleW(h, sb, 1024, 0) == 0) return null;
                        string p = sb.ToString();
                        if (p.StartsWith(@"\\?\UNC\")) p = @"\\" + p.Substring(8);
                        else if (p.StartsWith(@"\\?\")) p = p.Substring(4);
                        if (p.IndexOf(@"\$Recycle.Bin\", StringComparison.OrdinalIgnoreCase) >= 0) return null;   // deleted
                        return (File.Exists(p) || Directory.Exists(p)) ? p : null;
                    }
                }
            }
        }
        catch { }
        return null;
    }
}

static class Native
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct SHFILEINFO
    {
        public IntPtr hIcon;
        public int iIcon;
        public uint dwAttributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szDisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string szTypeName;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr SHGetFileInfo(string path, uint attr, ref SHFILEINFO psfi, uint cb, uint flags);
    [DllImport("shell32.dll")]
    public static extern int SHGetImageList(int iImageList, ref Guid riid, out IntPtr ppv);
    [DllImport("shell32.dll")]
    public static extern void SHChangeNotify(int eventId, uint flags, IntPtr i1, IntPtr i2);
    [DllImport("shell32.dll", EntryPoint = "SHChangeNotify", CharSet = CharSet.Unicode)]
    static extern void SHChangeNotifyPath(int eventId, uint flags, string path, IntPtr i2);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] static extern int Shell_GetCachedImageIndexW(string iconPath, int iconIndex, uint flags);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] static extern void SHUpdateImageW(string hashItem, int iconIndex, uint flags, int imageIndex);

    // Tells Explorer a file changed so it redraws its icon. A folder's icon is drawn from the
    // file itself, and Explorer keeps that picture cached under the file's name - so besides
    // "this file changed" it also needs "the image for this icon changed" (SHUpdateImage), or it
    // keeps showing the old mini-grid until it restarts.
    public static void NotifyFileChanged(string path)
    {
        try
        {
            // the icon is cached under "this file, icon 0", marked per-instance (GIL_PERINSTANCE);
            // send both forms to be sure Explorer drops its copy
            foreach (uint flags in new uint[] { 0x0002 /*GIL_PERINSTANCE*/, 0 })
            {
                int cached = Shell_GetCachedImageIndexW(path, 0, flags);
                SHUpdateImageW(path, 0, flags, cached >= 0 ? cached : 0);
            }
        }
        catch { }
        try { SHChangeNotifyPath(0x2000 /*SHCNE_UPDATEITEM*/, 0x0005 /*SHCNF_PATHW*/, path, IntPtr.Zero); } catch { }
        // and the strong one: "icons changed" - Explorer reloads its icons, ours included
        try { SHChangeNotify(0x08000000 /*SHCNE_ASSOCCHANGED*/, 0x0000 /*SHCNF_IDLIST*/, IntPtr.Zero, IntPtr.Zero); } catch { }
    }
    [DllImport("comctl32.dll")]
    public static extern IntPtr ImageList_GetIcon(IntPtr himl, int i, int flags);
    [DllImport("user32.dll")]
    public static extern bool DestroyIcon(IntPtr h);
    [DllImport("user32.dll")]
    public static extern bool SetProcessDPIAware();
    [DllImport("dwmapi.dll")]
    public static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
    [DllImport("gdi32.dll")]
    public static extern IntPtr CreateRoundRectRgn(int x1, int y1, int x2, int y2, int w, int h);

    public static bool Jumbo;   // 256px icons (only for the 2x README pictures)
    const uint SHGFI_SYSICONINDEX = 0x4000, SHGFI_USEFILEATTRIBUTES = 0x10;
    const int SHIL_EXTRALARGE = 2;

    // Icons already looked up (the hover helper keeps these, so folders open without asking
    // Windows for every icon again). A missing file isn't cached, so it shows up once it exists.
    static readonly Dictionary<string, Image> iconCache = new Dictionary<string, Image>(StringComparer.OrdinalIgnoreCase);

    // Returns the 48x48 shell icon for a file, folder, shortcut or URL.
    public static Image GetIcon(string path)
    {
        Image img;
        if (iconCache.TryGetValue(path, out img)) return img;
        img = LoadIcon(path);
        if (img != null) iconCache[path] = img;
        return img;
    }

    static Image LoadIcon(string path)
    {
        try
        {
            var info = new SHFILEINFO();
            uint flags = SHGFI_SYSICONINDEX;
            string query = path;
            uint attr = 0;
            if (IsUrl(path)) { query = ".url"; flags |= SHGFI_USEFILEATTRIBUTES; attr = 0x80; }
            else if (!File.Exists(path) && !Directory.Exists(path)) return null;
            if (SHGetFileInfo(query, attr, ref info, (uint)Marshal.SizeOf(info), flags) == IntPtr.Zero) return null;
            Guid iid = new Guid("46EB5926-582E-4017-9FDF-E8998DAA0950");
            IntPtr list;
            if (SHGetImageList(Jumbo ? 4 /*SHIL_JUMBO, 256px*/ : SHIL_EXTRALARGE, ref iid, out list) != 0) return null;
            IntPtr h = ImageList_GetIcon(list, info.iIcon, 1);
            if (h == IntPtr.Zero) return null;
            using (var ic = System.Drawing.Icon.FromHandle(h))
            {
                Image bmp = ic.ToBitmap();
                DestroyIcon(h);
                return bmp;
            }
        }
        catch { return null; }
    }

    public static bool IsUrl(string s)
    {
        return s.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
               s.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
    }
}

// User settings, shared by every AppFolder window and the hover helper (HKCU\Software\AppFolder).
static class Settings
{
    const string Key = @"Software\AppFolder";

    static int Get(string name, int def)
    {
        try { using (var k = Registry.CurrentUser.OpenSubKey(Key)) return k == null ? def : Convert.ToInt32(k.GetValue(name, def)); }
        catch { return def; }
    }
    static void Set(string name, int v)
    {
        try { using (var k = Registry.CurrentUser.CreateSubKey(Key)) k.SetValue(name, v, RegistryValueKind.DWord); } catch { }
    }

    public static bool HoverOpen { get { return Get("HoverOpen", 1) != 0; } set { Set("HoverOpen", value ? 1 : 0); } }
    public static bool Glass { get { return Get("Glass", 1) != 0; } set { Set("Glass", value ? 1 : 0); } }
    // Desktop icon: 3x3 mini-grid once a folder has more than 4 apps (true), or always 2x2 (false)
    public static bool IconGrid3 { get { return Get("IconGrid3", 1) != 0; } set { Set("IconGrid3", value ? 1 : 0); } }
    // Animations: folders, menus and cards fade/spring in and out (true), or appear instantly (false)
    public static bool Animations { get { return Get("Animations", 1) != 0; } set { Set("Animations", value ? 1 : 0); } }
    // Corners: big and round like Prism (false), or tighter (true). Both looks, and the Desktop icon.
    public static bool SharpCorners { get { return Get("SharpCorners", 0) != 0; } set { Set("SharpCorners", value ? 1 : 0); } }

    static string GetS(string name)
    {
        try { using (var k = Registry.CurrentUser.OpenSubKey(Key)) return k == null ? "" : (k.GetValue(name, "") as string ?? ""); }
        catch { return ""; }
    }
    static void SetS(string name, string v)
    {
        try { using (var k = Registry.CurrentUser.CreateSubKey(Key)) k.SetValue(name, v ?? ""); } catch { }
    }

    // what the last update check found (see Updater)
    public static string LatestVersion { get { return GetS("LatestVersion"); } set { SetS("LatestVersion", value); } }
    public static string LatestUrl { get { return GetS("LatestUrl"); } set { SetS("LatestUrl", value); } }
    public static string LastUpdateCheck { get { return GetS("LastUpdateCheck"); } set { SetS("LastUpdateCheck", value); } }
    public static string NotifiedVersion { get { return GetS("NotifiedVersion"); } set { SetS("NotifiedVersion", value); } }
}

// Updates: checks this app's GitHub releases and installs a newer one in place.
// To push an update to everyone: publish a new release on GitHub (tag "vX.Y.Z") with the .exe
// attached. Apps find it within a day (or straight away with Settings > Check), download it from
// the release and run it as "--install --quiet --updated", which replaces the installed copy.
// Only GitHub's public release list for this repo is read, and only this repo's release files
// are downloaded; nothing about the user is sent.
static class Updater
{
    public const string Repo = "Peteboii/Prysm-Folder";
    public static readonly Version Current = new Version(1, 3, 2);
    public static string CurrentText { get { return Current.ToString(3); } }

    public class Release { public Version Version; public string ExeUrl; }

    static void Tls() { ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12; }

    // The newest release on GitHub (throws if it can't be reached or read).
    public static Release FetchLatest()
    {
        Tls();
        var req = (HttpWebRequest)WebRequest.Create("https://api.github.com/repos/" + Repo + "/releases/latest");
        req.UserAgent = "PrysmFolder/" + CurrentText;   // GitHub requires one; no personal details
        req.Accept = "application/vnd.github+json";
        req.Timeout = 15000;
        string json;
        using (var resp = req.GetResponse())
        using (var sr = new StreamReader(resp.GetResponseStream())) json = sr.ReadToEnd();
        string tag = Regex.Match(json, "\"tag_name\"\\s*:\\s*\"([^\"]+)\"").Groups[1].Value;
        string exe = Regex.Match(json, "\"browser_download_url\"\\s*:\\s*\"([^\"]+\\.exe)\"").Groups[1].Value;
        Version v;
        if (!Version.TryParse(tag.TrimStart('v', 'V'), out v)) throw new Exception("The latest release has an unexpected tag: " + tag);
        if (exe.Length == 0) throw new Exception("The latest release has no .exe attached.");
        return new Release { Version = v, ExeUrl = exe };
    }

    public static void Remember(Release r)
    {
        Settings.LatestVersion = r.Version.ToString(3);
        Settings.LatestUrl = r.ExeUrl;
        Settings.LastUpdateCheck = DateTime.UtcNow.ToString("o");
    }

    public static Version Latest { get { Version v; return Version.TryParse(Settings.LatestVersion, out v) ? v : null; } }
    public static bool Available { get { var v = Latest; return v != null && v > Current && Settings.LatestUrl.Length > 0; } }

    public static bool CheckedRecently(TimeSpan within)
    {
        DateTime t;
        return DateTime.TryParse(Settings.LastUpdateCheck, null, System.Globalization.DateTimeStyles.RoundtripKind, out t) && DateTime.UtcNow - t < within;
    }

    // Downloads the update to %TEMP% (reporting 0-100). Only from this repo's own releases.
    public static string Download(string url, Action<int> progress)
    {
        if (!url.StartsWith("https://github.com/" + Repo + "/releases/download/", StringComparison.OrdinalIgnoreCase))
            throw new Exception("Refusing to download from an unexpected address.");
        Tls();
        string path = Path.Combine(Path.GetTempPath(), "PrysmFolder-update.exe");
        var req = (HttpWebRequest)WebRequest.Create(url);
        req.UserAgent = "PrysmFolder/" + CurrentText;
        req.Timeout = 30000;
        req.ReadWriteTimeout = 30000;   // a stalled connection fails instead of hanging at "Downloading… 40%"
        using (var resp = (HttpWebResponse)req.GetResponse())
        using (var src = resp.GetResponseStream())
        using (var dst = File.Create(path))
        {
            long total = resp.ContentLength, got = 0;
            var buf = new byte[81920];
            int n, last = -1;
            while ((n = src.Read(buf, 0, buf.Length)) > 0)
            {
                dst.Write(buf, 0, n);
                got += n;
                int pct = total > 0 ? (int)(got * 100 / total) : 0;
                if (pct != last && progress != null) { progress(pct); last = pct; }
            }
            // a dropped connection can end the stream early: never install half a program
            if (total > 0 && got != total) throw new Exception("The download was cut off. Try again.");
        }
        var head = new byte[2];
        using (var f = File.OpenRead(path)) f.Read(head, 0, 2);
        if (new FileInfo(path).Length < 50 * 1024 || head[0] != 'M' || head[1] != 'Z')
            throw new Exception("The download doesn't look like Prysm Folder.");
        return path;
    }

    // Runs the downloaded version's installer; it replaces this copy and restarts the helper.
    public static void Install(string exe)
    {
        Process.Start(new ProcessStartInfo(exe, "--install --quiet --updated") { UseShellExecute = false });
    }

    // Background check (the hover helper calls this): at most once a day; tells the user once per version.
    public static void BackgroundCheck(Control ui)
    {
        if (CheckedRecently(TimeSpan.FromHours(20))) { NotifyOnce(ui); return; }
        var t = new Thread(() =>
        {
            try { Remember(FetchLatest()); }
            catch (Exception ex) { HoverWatcher.Log("update check failed: " + ex.Message); return; }
            try { ui.BeginInvoke((Action)(() => NotifyOnce(ui))); } catch { }
        }) { IsBackground = true };
        t.Start();
    }

    static void NotifyOnce(Control ui)
    {
        if (!Available || Settings.NotifiedVersion == Settings.LatestVersion) return;
        Settings.NotifiedVersion = Settings.LatestVersion;
        Toast.Show("Prysm Folder " + Settings.LatestVersion + " is available", "Open any folder's settings (the gear) to update.");
    }
}

// A small Prism-style notice in the bottom-right corner that fades away by itself.
class Toast : Form
{
    readonly string title, body;
    readonly Font titleFont = Theme.UiFont(10f, FontStyle.Regular, true), bodyFont = Theme.UiFont(9f);

    public static void Show(string title, string body)
    {
        try { Theme.Load(); new Toast(title, body).Show(); } catch { }
    }

    // the same notice as the whole app (after an update installs: the process ends when it closes)
    public class Done : Toast { public Done(string title, string body) : base(title, body) { } }

    protected Toast(string title, string body)
    {
        this.title = title; this.body = body;
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        ShowInTaskbar = false;
        TopMost = true;
        BackColor = Theme.Panel;
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint, true);
        float S = FolderForm.S;
        ClientSize = new Size((int)(340 * S), (int)(74 * S));
        var wa = Screen.PrimaryScreen.WorkingArea;
        Location = new Point(wa.Right - Width - (int)(16 * S), wa.Bottom - Height - (int)(16 * S));
        Cursor = Cursors.Hand;
        Click += (o, e) => Close();
    }

    protected override CreateParams CreateParams
    {
        get { var cp = base.CreateParams; cp.ExStyle |= 0x08000000 | 0x80; cp.ClassStyle |= 0x20000; return cp; }   // no focus, no taskbar, shadow
    }
    protected override bool ShowWithoutActivation { get { return true; } }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        int round = 2, rim = Theme.ToBgr(Theme.Rim), dark = Theme.Light ? 0 : 1;
        try
        {
            Native.DwmSetWindowAttribute(Handle, 33, ref round, 4);
            Native.DwmSetWindowAttribute(Handle, 34, ref rim, 4);
            Native.DwmSetWindowAttribute(Handle, 20, ref dark, 4);
        }
        catch { }
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        Opacity = 0;
        var clock = Stopwatch.StartNew();
        var t = new System.Windows.Forms.Timer { Interval = 15 };
        t.Tick += (o, a) =>
        {
            double ms = clock.Elapsed.TotalMilliseconds;
            if (IsDisposed) { t.Dispose(); return; }
            Opacity = !Settings.Animations ? (ms < 6000 ? 1 : 0) : ms < 200 ? ms / 200 : ms < 6000 ? 1 : Math.Max(0, 1 - (ms - 6000) / 400);
            if (ms > 6400) { t.Dispose(); Close(); }
        };
        t.Start();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Theme.Panel);
        float S = FolderForm.S;
        using (var ic = Promo.AppIcon(64)) { g.InterpolationMode = InterpolationMode.HighQualityBicubic; g.DrawImage(ic, 14 * S, 17 * S, 40 * S, 40 * S); }
        var tr = new Rectangle((int)(66 * S), (int)(14 * S), Width - (int)(80 * S), (int)(24 * S));
        TextRenderer.DrawText(g, title, titleFont, tr, Theme.Fg, TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        var br = new Rectangle((int)(66 * S), (int)(38 * S), Width - (int)(80 * S), (int)(24 * S));
        TextRenderer.DrawText(g, body, bodyFont, br, Theme.Dim, TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
    }
}

// Prism's look (same palette as DG-Dock's launchpad): a #202022 panel, white "ink" tints and
// hairlines on top of it, #4cc2ff accent, Segoe UI Variable. Follows Windows light/dark mode.
static class Theme
{
    public static bool Light, Glass;
    public static Color Panel, Fg, Fg2, Dim, Ink05, Ink07, Ink08, Ink12, Ink18, Line, Rim;
    public static readonly Color Accent = Color.FromArgb(0x4c, 0xc2, 0xff);

    // See-through look: the window's background is this colour made fully transparent, with a
    // blurred (acrylic) window behind it. Odd values so real icon pixels practically never match.
    // (On the Solid look the key is the panel colour itself: the card layer behind is that same
    // colour, so it looks identical, while letting both looks share the same smooth corners.)
    public static Color Key { get { return !Glass ? Panel : Light ? Color.FromArgb(0xee, 0xed, 0xf1) : Color.FromArgb(0x1f, 0x1e, 0x21); } }
    // What every control paints its background with.
    public static Color Bg { get { return Glass ? Key : Panel; } }
    // Acrylic tint, as AABBGGRR: the Prism panel colour at ~60% over the blur.
    public static int GlassTint { get { return Light ? unchecked((int)0x99F5F3F3) : unchecked((int)0x9C222020); } }

    public static float FontScale = 1f;   // >1 only when drawing the README pictures at 2x

    public static void Load()
    {
        bool light;
        try
        {
            using (var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                light = k != null && Convert.ToInt32(k.GetValue("AppsUseLightTheme", 0)) == 1;
        }
        catch { light = false; }
        Grid3 = Settings.IconGrid3;
        Sharp = Settings.SharpCorners;
        Apply(light, Settings.Glass);
    }

    public static bool Grid3 = true;   // the Desktop icon's mini-grid can go 3x3 (see Settings.IconGrid3)
    public static bool Sharp;          // tighter corners (see Settings.SharpCorners)

    // A corner radius (in Prism's "rounded" size), made tighter when Sharper corners is on.
    public static float Radius(float rounded) { return Sharp ? Math.Max(4f, rounded / 3f) : rounded; }

    public static void Apply(bool light, bool glass)
    {
        Light = light;
        Glass = glass;
        if (Light)
        {
            Panel = Color.FromArgb(0xf3, 0xf3, 0xf5); Fg = Color.FromArgb(0x17, 0x17, 0x1b);
            Fg2 = Mix(Color.Black, .80); Dim = Mix(Color.Black, .60);
            Ink05 = Mix(Color.Black, .035); Ink07 = Mix(Color.Black, .05); Ink08 = Mix(Color.Black, .06);
            Ink12 = Mix(Color.Black, .09); Ink18 = Mix(Color.Black, .13); Line = Mix(Color.Black, .08); Rim = Mix(Color.Black, .12);
        }
        else
        {
            Panel = Color.FromArgb(0x20, 0x20, 0x22); Fg = Color.FromArgb(0xf4, 0xf4, 0xf7);
            Fg2 = Mix(Color.White, .84); Dim = Mix(Color.White, .66);
            Ink05 = Mix(Color.White, .045); Ink07 = Mix(Color.White, .07); Ink08 = Mix(Color.White, .08);
            Ink12 = Mix(Color.White, .12); Ink18 = Mix(Color.White, .18); Line = Mix(Color.White, .08); Rim = Mix(Color.White, .13);
        }
        if (Glass) { Fg2 = Fg; Rim = Light ? Mix(Color.Black, .10) : Mix(Color.White, .20); }   // crisper text, brighter rim on glass
    }

    // A tint of c at alpha a laid over the panel, as a solid colour (WinForms needs solid colours).
    static Color Mix(Color c, double a)
    {
        Color p = Panel;
        return Color.FromArgb((int)(p.R + (c.R - p.R) * a), (int)(p.G + (c.G - p.G) * a), (int)(p.B + (c.B - p.B) * a));
    }

    // Fonts are made once and shared (nothing disposes them): every folder the hover helper opens
    // used to create new ones, trying several names each time, and leave them for the garbage collector.
    static readonly Dictionary<string, Font> fonts = new Dictionary<string, Font>();

    public static Font UiFont(float size, FontStyle style = FontStyle.Regular, bool display = false)
    {
        string key = size + "|" + style + "|" + display + "|" + FontScale;
        Font cached;
        if (fonts.TryGetValue(key, out cached)) return cached;
        return fonts[key] = MakeUiFont(size, style, display);
    }

    static Font MakeUiFont(float size, FontStyle style, bool display)
    {
        foreach (string name in display
            ? new[] { "Segoe UI Variable Display Semib", "Segoe UI Variable Display", "Segoe UI Semibold", "Segoe UI" }
            : new[] { "Segoe UI Variable Text", "Segoe UI" })
        {
            try
            {
                var f = new Font(name, size * FontScale, style);
                if (f.Name == name) return f;
                f.Dispose();
            }
            catch { }
        }
        return new Font("Segoe UI", size * FontScale, style);
    }

    public static int ToBgr(Color c) { return c.R | (c.G << 8) | (c.B << 16); }

    // The look a Desktop icon is drawn in; when it changes, the icons are redrawn.
    public static string LookSig { get { return (Light ? "light" : "dark") + "-" + (Glass ? "glass" : "solid") + "-g" + (Grid3 ? "3" : "2") + "-c" + (Sharp ? "s" : "r") + "-2"; } }

    // Windows 11's icon font (Segoe Fluent Icons), or Segoe MDL2 Assets on older systems
    public static Font IconFont(float size)
    {
        string key = "icon|" + size + "|" + FontScale;
        Font cached;
        if (fonts.TryGetValue(key, out cached)) return cached;
        var f = new Font("Segoe Fluent Icons", size * FontScale);
        if (f.Name != "Segoe Fluent Icons") { f.Dispose(); f = new Font("Segoe MDL2 Assets", size * FontScale); }
        return fonts[key] = f;
    }
}

// Runs a short animation in step with the screen's refresh: each step waits for Windows'
// compositor to show the next frame (DwmFlush), so a fade advances evenly on every refresh
// instead of in the uneven ~15 ms jumps of a timer. Short (100-200 ms), so it simply runs.
static class Anim
{
    [DllImport("dwmapi.dll")] static extern int DwmFlush();

    public static void Run(double ms, Action<double> step)
    {
        var sw = Stopwatch.StartNew();
        while (true)
        {
            double f = Math.Min(1, sw.Elapsed.TotalMilliseconds / ms);
            step(f);
            if (f >= 1) break;
            if (DwmFlush() != 0) Thread.Sleep(4);
        }
    }

    public static double EaseOut(double f) { return 1 - Math.Pow(1 - f, 3); }
    public static double EaseIn(double f) { return f * f; }
    public static double EaseOutQuint(double f) { return 1 - Math.Pow(1 - f, 5); }

    [DllImport("user32.dll")] static extern bool RedrawWindow(IntPtr hwnd, IntPtr rect, IntPtr rgn, uint flags);

    // Animations off: paint the whole window (children too) while it's still invisible, let the
    // compositor take that finished frame (and the glass behind it), and only then make it visible,
    // so the first thing on screen is the finished window, never a blank flash.
    public static void ShowNow(Form f)
    {
        try
        {
            RedrawWindow(f.Handle, IntPtr.Zero, IntPtr.Zero, 0x1 | 0x100 | 0x80);   // INVALIDATE | UPDATENOW | ALLCHILDREN
            DwmFlush();
            DwmFlush();
        }
        catch { }
        if (!f.IsDisposed) f.Opacity = 1;
    }
}

// One shape on a control (a pill, a ring, a highlight, a line). Controls describe their shapes
// as these; on glass they're drawn by Windows' vector renderer in the card layer behind (smooth
// edges), elsewhere they're painted with GDI+ as usual. (Painted shapes on the see-through window
// can't be smooth: their soft edge pixels blend into the invisible background colour.)
class Deco
{
    public RectangleF R; public float Radius;
    public Color Fill = Color.Empty, Stroke = Color.Empty; public float StrokeW = 1f;
    public bool Ellipse, Dashed, Line, ClipToGrid;
    public PointF A, B;

    public static Deco Box(RectangleF r, float radius, Color fill) { return new Deco { R = r, Radius = radius, Fill = fill }; }
    public static Deco Outline(RectangleF r, float radius, Color stroke, float w) { return new Deco { R = r, Radius = radius, Stroke = stroke, StrokeW = w }; }
    public static Deco Circle(RectangleF r, Color fill, Color stroke, float w) { return new Deco { R = r, Ellipse = true, Fill = fill, Stroke = stroke, StrokeW = w }; }
    public static Deco Ln(float x1, float y1, float x2, float y2, Color c, float w) { return new Deco { Line = true, A = new PointF(x1, y1), B = new PointF(x2, y2), Stroke = c, StrokeW = w }; }

    public void PaintGdi(Graphics g)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        if (Line)
        {
            using (var p = new Pen(Stroke, StrokeW) { StartCap = LineCap.Round, EndCap = LineCap.Round }) g.DrawLine(p, A, B);
            return;
        }
        using (var path = Ellipse ? EllipsePath(R) : FolderForm.Rounded(R, Radius))
        {
            if (!Fill.IsEmpty) using (var b = new SolidBrush(Fill)) g.FillPath(b, path);
            if (!Stroke.IsEmpty)
                using (var p = new Pen(Stroke, StrokeW)) { if (Dashed) p.DashPattern = new[] { 3f, 2.5f }; g.DrawPath(p, path); }
        }
    }
    static GraphicsPath EllipsePath(RectangleF r) { var p = new GraphicsPath(); p.AddEllipse(r); return p; }
}

// A window whose control shapes can be drawn in its glass layer (see Deco).
interface IDecoHost
{
    bool DecosOn { get; }                // shapes go to the glass layer (and GDI skips them)
    void CollectDecos(List<Deco> list);  // every shape, in this window's coordinates
    RectangleF DecoClip { get; }         // where ClipToGrid shapes may show (a scrolling area)
    void RequestDecos();                 // something changed: redraw the shapes soon
}

static class Decor
{
    public static bool Disabled;   // off for the README pictures (drawn without the glass layer)

    static IDecoHost HostOf(Control c) { return c == null ? null : c.FindForm() as IDecoHost; }
    public static bool On(Control c) { var h = HostOf(c); return !Disabled && h != null && h.DecosOn; }
    public static void Request(Control c) { var h = HostOf(c); if (h != null) h.RequestDecos(); }
    public static void Paint(Graphics g, List<Deco> list) { foreach (var d in list) d.PaintGdi(g); }
}

// Round 28px button with an X (or a gear), like the buttons on Prism's cards.
class CloseButton : Control
{
    bool hover;
    public bool Gear;
    static Font gearFont;
    public CloseButton()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        Cursor = Cursors.Hand;
        TabStop = false;
    }
    protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { hover = false; Invalidate(); base.OnMouseLeave(e); }
    // on glass the pointer over the see-through middle reaches the glass, which tells us
    public void SetHover(bool on) { if (hover != on) { hover = on; Invalidate(); } }
    // the circle (red on hover for the X) and the X itself
    public void AddDecos(List<Deco> list, float ox, float oy)
    {
        float s = FolderForm.S, W = Width, H = Height;
        bool closeHot = hover && !Gear;
        if (closeHot)   // the X turns red on hover, like Windows 11's close button
            list.Add(Deco.Circle(new RectangleF(ox + 0.5f, oy + 0.5f, W - 2, H - 2), Color.FromArgb(0xc4, 0x2b, 0x1c), Color.Empty, 0));
        else if (Theme.Glass)   // on glass: a frosted-looking ring (a solid disc would read as a grey sticker)
            list.Add(Deco.Circle(new RectangleF(ox + 1, oy + 1, W - 3, H - 3), Color.Empty, hover ? Theme.Dim : Theme.Ink18, 1.2f * s));
        else
            list.Add(Deco.Circle(new RectangleF(ox, oy, W - 1, H - 1), hover ? Theme.Ink12 : Theme.Ink08, Color.Empty, 0));
        if (Gear && Updater.Available)   // an update is waiting: a little Prism-blue dot on the gear
        {
            float d = 8 * s;
            list.Add(Deco.Circle(new RectangleF(ox + W - d - 1 * s, oy + 1 * s, d, d), Theme.Accent, Color.Empty, 0));
        }
        if (!Gear)
        {
            float c = (W - 1) / 2f, r = W * 0.16f;   // the ring's own centre
            Color col = closeHot ? Color.White : Theme.Fg2;
            list.Add(Deco.Ln(ox + c - r, oy + c - r, ox + c + r, oy + c + r, col, 1.5f * s));
            list.Add(Deco.Ln(ox + c - r, oy + c + r, ox + c + r, oy + c - r, col, 1.5f * s));
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Theme.Bg);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        if (Decor.On(this)) Decor.Request(this);
        else { var l = new List<Deco>(); AddDecos(l, 0, 0); Decor.Paint(g, l); }
        if (Gear)
        {
            // Windows 11's own settings glyph (Segoe Fluent Icons; MDL2 Assets on older systems)
            if (gearFont == null)
            {
                gearFont = Theme.IconFont(9.5f);
            }
            FolderForm.TextCentered(g, "", gearFont, GearRect(), hover ? Theme.Fg : Theme.Fg2);
        }
    }

    // The gear glyph's ink isn't centred in its font box: measure where it really lands once and
    // shift it so its middle sits exactly on the ring's middle.
    Size gearShiftFor; Point gearShift;
    Rectangle GearRect()
    {
        if (gearShiftFor != Size) { gearShift = InkShift("", gearFont, Size); gearShiftFor = Size; }
        return new Rectangle(gearShift.X, gearShift.Y, Width, Height);
    }

    static Point InkShift(string glyph, Font font, Size size)
    {
        using (var bmp = new Bitmap(size.Width, size.Height))
        {
            using (var g = Graphics.FromImage(bmp))
            {
                g.Clear(Color.White);
                FolderForm.TextCentered(g, glyph, font, new Rectangle(Point.Empty, size), Color.Black);
            }
            int x0 = size.Width, y0 = size.Height, x1 = -1, y1 = -1;
            for (int y = 0; y < size.Height; y++)
                for (int x = 0; x < size.Width; x++)
                {
                    var p = bmp.GetPixel(x, y);
                    if (p.R + p.G + p.B < 600) { x0 = Math.Min(x0, x); x1 = Math.Max(x1, x); y0 = Math.Min(y0, y); y1 = Math.Max(y1, y); }
                }
            if (x1 < 0) return Point.Empty;
            // the ring is centred on (size-1)/2 (edge coordinates); so is the ink after the shift
            double inkX = (x0 + x1 + 1) / 2.0, inkY = (y0 + y1 + 1) / 2.0;
            return new Point((int)Math.Round((size.Width - 1) / 2.0 - inkX), (int)Math.Round((size.Height - 1) / 2.0 - inkY));
        }
    }
}

class Tile : Control
{
    public Item Item;     // null = the "+" tile
    public bool Hover;
    public Point DownAt;
    public bool Dragging;

    public Tile()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        BackColor = Theme.Panel;
        Cursor = Cursors.Hand;
        AllowDrop = true;
    }

    public bool DropTarget;   // highlighted while something is dragged over it
    public bool Lifted;       // being dragged to a new spot

    protected override void OnMouseEnter(EventArgs e) { Hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { Hover = false; Invalidate(); base.OnMouseLeave(e); }

    Rectangle IconRect
    {
        get { int iconSize = (int)(48 * FolderForm.S); return new Rectangle((Width - iconSize) / 2, (int)(12 * FolderForm.S), iconSize, iconSize); }
    }

    // this tile's shapes: hover / picked-up outline, drop ring, the dashed "+" slot
    public void AddDecos(List<Deco> list, float ox, float oy)
    {
        float s = FolderForm.S;
        var whole = new RectangleF(ox + 2 * s, oy + 2 * s, Width - 4 * s, Height - 4 * s);
        float rad = Theme.Radius(16) * s;
        var mine = new List<Deco>();
        if (Lifted) mine.Add(Deco.Outline(whole, rad, Theme.Accent, 2f * s));   // picked up: Prism blue
        else if (Hover || DropTarget)
            mine.Add(Theme.Glass ? Deco.Outline(whole, rad, Theme.Ink18, 1.2f * s)   // glass: a soft outline
                                 : Deco.Box(whole, rad, Theme.Ink07));
        var ic = IconRect;
        ic.Offset((int)ox, (int)oy);
        if (DropTarget)
        {
            var ring = RectangleF.Inflate(ic, 3 * s, 3 * s);
            mine.Add(Deco.Outline(ring, ring.Width * 0.24f, Theme.Accent, 2.5f * s));
        }
        if (Item == null)
        {
            // Prism's drop slot: dashed rounded square with a plus
            var r = RectangleF.Inflate(ic, -2 * s, -2 * s);
            var dash = Deco.Outline(r, r.Width * 0.24f, Hover ? Theme.Dim : Color.FromArgb(115, 128, 128, 128), 1.5f * s);
            dash.Dashed = true;
            mine.Add(dash);
            float c = r.X + r.Width / 2f, m = r.Y + r.Height / 2f, k = r.Width * 0.18f;
            Color plus = Hover ? Theme.Fg : Theme.Dim;
            mine.Add(Deco.Ln(c - k, m, c + k, m, plus, 1.6f * s));
            mine.Add(Deco.Ln(c, m - k, c, m + k, plus, 1.6f * s));
        }
        else if (Item.Icon == null)
            mine.Add(Deco.Box(ic, ic.Width * 0.22f, Theme.Ink08));   // icon not found
        foreach (var d in mine) d.ClipToGrid = true;
        list.AddRange(mine);
    }

    protected override void OnLocationChanged(EventArgs e) { base.OnLocationChanged(e); if (Decor.On(this)) Decor.Request(this); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.Clear(Theme.Bg);
        float s = FolderForm.S;
        if (Decor.On(this)) Decor.Request(this);
        else { var l = new List<Deco>(); AddDecos(l, 0, 0); Decor.Paint(g, l); }
        var iconRect = IconRect;
        if (Item != null && Item.Icon != null) g.DrawImage(Item.Icon, iconRect);
        else if (Item != null)
            TextRenderer.DrawText(g, "?", Font, iconRect, Theme.Dim, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        string label = Item == null ? "Add" : Item.Label;
        var textRect = new Rectangle((int)(4 * s), iconRect.Bottom + (int)(6 * s), Width - (int)(8 * s), Height - iconRect.Bottom - (int)(8 * s));
        FolderForm.DrawLabel(g, label, Font, textRect, Item == null ? Theme.Dim : Theme.Fg2, true);
    }
}

class FolderForm : Form, IGlassHost, IDecoHost
{
    // ---- control shapes in the glass layer (see Deco) ----
    public bool DecosOn { get { return !Decor.Disabled && BackdropAlive && backdrop.Rounded; } }
    public RectangleF DecoClip { get { return grid.Bounds; } }
    bool decosPending;

    public void RequestDecos()
    {
        if (decosPending || !IsHandleCreated || IsDisposed) return;
        decosPending = true;
        BeginInvoke((Action)(() => { decosPending = false; if (DecosOn) backdrop.UpdateDecos(); }));
    }

    public void CollectDecos(List<Deco> list)
    {
        closeBtn.AddDecos(list, closeBtn.Left, closeBtn.Top);
        gearBtn.AddDecos(list, gearBtn.Left, gearBtn.Top);
        foreach (Control c in grid.Controls)
        {
            var t = c as Tile;
            if (t != null && t.Visible) t.AddDecos(list, grid.Left + t.Left, grid.Top + t.Top);
        }
    }

    public static float S = 1f;   // DPI scale
    const int Cols = 4, MaxRows = 4;

    readonly string id, cfgPath, dataDir;
    string filePath;   // set when this folder is a .appfolder file (e.g. on the Desktop)
    string title = "My Folder";
    bool busy;
    readonly List<Item> items = new List<Item>();
    readonly TextBox titleBox = new TextBox();
    readonly Panel grid = new Panel();
    readonly CloseButton closeBtn = new CloseButton();
    readonly CloseButton gearBtn = new CloseButton { Gear = true };
    Tile dropTile, glassHoverTile;
    Rectangle titleRect;
    GlassBackdrop backdrop;   // the blurred window behind this one in the See-through look

    public bool HoverMode;   // opened by pointing at it: closes again when the pointer leaves
    public bool SpringMode;  // sprang open during a drag: appears without taking focus from the drag
    protected override bool ShowWithoutActivation { get { return SpringMode; } }
    public static bool OpenedByHover;   // --hover on the command line
    public Point? OpenAt;    // where to put the window (reopening after a settings change)
    public string FilePath { get { return filePath; } }
    public List<Item> PromoItems { get { return items; } }   // for the README pictures
    readonly System.Windows.Forms.Timer hoverTimer = new System.Windows.Forms.Timer { Interval = 50 };
    DateTime pointerAwaySince = DateTime.MaxValue;

    static int Px(float v) { return (int)Math.Round(v * S); }
    int TileW { get { return Px(88); } }
    int TileH { get { return Px(104); } }

    public FolderForm(string id, string filePath)
    {
        this.id = id;
        this.filePath = filePath;
        dataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), Installer.AppName);
        Directory.CreateDirectory(dataDir);
        cfgPath = Path.Combine(dataDir, id + ".txt");

        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        BackColor = Theme.Bg;
        TransparencyKey = Theme.Key;   // background shows the card layer behind (glass, or the solid panel)
        ForeColor = Theme.Fg;
        Font = Theme.UiFont(9f);
        KeyPreview = true;
        AllowDrop = true;
        ShowInTaskbar = false;
        TopMost = true;   // pops up over everything, like a phone folder
        Text = title;
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint, true);

        // The name is drawn by the window; the text box only appears while you rename it.
        titleBox.BorderStyle = BorderStyle.None;
        titleBox.BackColor = Theme.Ink08;
        titleBox.ForeColor = Theme.Fg;
        titleBox.Font = Theme.UiFont(12f, FontStyle.Regular, true);
        titleBox.TextAlign = HorizontalAlignment.Left;
        titleBox.TabStop = false;
        titleBox.Visible = false;
        titleBox.KeyDown += (s, e) =>
        {
            if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; grid.Focus(); }
        };
        titleBox.Leave += (s, e) => { CommitTitle(); titleBox.Visible = false; Invalidate(); };
        Controls.Add(titleBox);
        MouseDown += (s, e) => { if (e.Button == MouseButtons.Left && titleRect.Contains(e.Location)) BeginRename(); };

        closeBtn.Click += (s, e) => Close();
        Controls.Add(closeBtn);
        gearBtn.Click += (s, e) => ShowSettings();
        Controls.Add(gearBtn);

        hoverTimer.Tick += (s, e) => CheckPointerLeft();
        hoverTimer.Start();
        slideTimer.Tick += (s, e) => SlideTick();

        grid.BackColor = BackColor;
        grid.AllowDrop = true;
        grid.AutoScroll = true;
        grid.Scroll += (s, e) => RequestDecos();
        grid.MouseWheel += (s, e) => RequestDecos();
        Controls.Add(grid);

        foreach (Control c in new Control[] { this, grid })
        {
            c.DragEnter += (s, e) => OnDragOverAny(e);
            c.DragOver += (s, e) => OnDragOverAny(e);
            c.DragDrop += (s, e) => OnDropAny(e, null);
            c.MouseUp += (s, e) => { if (e.Button == MouseButtons.Right) ShowBackgroundMenu(); };
        }

        BuildMenus();
        LoadConfig();
        Rebuild();
        PositionNearCursor();
    }

    protected override void OnLoad(EventArgs e)
    {
        if (OpenAt.HasValue) Location = OpenAt.Value;
        Opacity = 0;   // fades in once shown (see AnimateIn)
        base.OnLoad(e);
        // the card layer behind: glass on See-through, the panel on Solid (smooth corners either way)
        {
            backdrop = new GlassBackdrop(this, 24);
            backdrop.Show();
            Owner = backdrop;   // owned windows always sit just above their owner: blur stays right behind us
            if (backdrop.Rounded)
            {
                // the glass has its own big rounded corners and rim: Windows' 8px corner and border would fight it
                int noRound = 1, none = unchecked((int)0xFFFFFFFE);
                try { Native.DwmSetWindowAttribute(Handle, 33, ref noRound, 4); Native.DwmSetWindowAttribute(Handle, 34, ref none, 4); } catch { }
            }
            Invalidate(true);   // controls now leave their shapes to the glass layer
            RequestDecos();
        }
    }

    bool BackdropAlive { get { return backdrop != null && !backdrop.IsDisposed; } }

    protected override void OnLocationChanged(EventArgs e) { base.OnLocationChanged(e); if (BackdropAlive) backdrop.Bounds = Bounds; }
    protected override void OnSizeChanged(EventArgs e)
    {
        base.OnSizeChanged(e);
        if (BackdropAlive) backdrop.Bounds = Bounds;
        if (Region != null) Region = RoundRegion();
    }
    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        base.OnFormClosed(e);
        CloseBackdrop();
    }
    protected override void Dispose(bool disposing)
    {
        Icon ownIcon = disposing && ownsIcon ? Icon : null;
        if (disposing)
        {
            CloseBackdrop();
            hoverTimer.Dispose();   // the hover helper opens many folders over a day: don't leave timers behind
            animTimer.Dispose();
            slideTimer.Dispose();
        }
        base.Dispose(disposing);
        if (ownIcon != null) { ownsIcon = false; ownIcon.Dispose(); }   // once the window is gone
    }
    void CloseBackdrop()
    {
        var b = backdrop;
        backdrop = null;
        if (b == null || b.IsDisposed) return;
        try { if (!IsDisposed) Owner = null; } catch { }   // unhook first so closing it can't touch us
        try { b.Close(); b.Dispose(); } catch { }
    }

    // Used by the glass window behind us: clicks and drops on see-through parts land there.
    public void GlassMouseUp(MouseEventArgs e) { GlassClick(e); }
    public void GlassDragOver(DragEventArgs e) { OnDragOverAny(e); }
    public void GlassDrop(DragEventArgs e) { OnDropAny(e, null); }
    public void GlassMouseDown(MouseEventArgs e)
    {
        var p = PointToClient(Cursor.Position);
        if (e.Button != MouseButtons.Left) return;
        if (titleRect.Contains(p)) { BeginRename(); return; }
        var t = TileAt(p);
        if (t != null) PressTile(t);   // may become a drag to reorder
    }

    // The tile (if any) at a point in this window's client area.
    Tile TileAt(Point client)
    {
        if (!grid.Bounds.Contains(client)) return null;
        var gp = grid.PointToClient(PointToScreen(client));
        foreach (Control c in grid.Controls)
            if (c is Tile && c.Visible && c.Bounds.Contains(gp)) return (Tile)c;
        return null;
    }

    // Hover over a tile's see-through part (the glass gets the pointer there, not the tile).
    public void GlassMouseMove(Point client)
    {
        if (pressTile != null && (Control.MouseButtons & MouseButtons.Left) != 0) { DragTileMove(); return; }
        closeBtn.SetHover(closeBtn.Bounds.Contains(client));
        gearBtn.SetHover(gearBtn.Bounds.Contains(client));
        var t = TileAt(client);
        if (t == glassHoverTile) return;
        if (glassHoverTile != null && !glassHoverTile.IsDisposed) { glassHoverTile.Hover = false; glassHoverTile.Invalidate(); }
        glassHoverTile = t;
        if (t != null) { t.Hover = true; t.Invalidate(); }
    }

    // A click on a see-through part: on a tile it opens the app (or its menu); elsewhere,
    // a right-click shows the folder's menu.
    void GlassClick(MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left && EndTileDrag()) return;   // finished a reorder
        var cp = PointToClient(Cursor.Position);
        if (e.Button == MouseButtons.Left && closeBtn.Bounds.Contains(cp)) { Close(); return; }
        if (e.Button == MouseButtons.Left && gearBtn.Bounds.Contains(cp)) { ShowSettings(); return; }
        var t = TileAt(cp);
        if (e.Button == MouseButtons.Left)
        {
            if (t == null) return;
            if (t.Item == null) ShowAddMenu(); else Launch(t.Item);
        }
        else if (e.Button == MouseButtons.Right)
        {
            if (t != null && t.Item != null) ShowTileMenu(t); else ShowBackgroundMenu();
        }
    }

    void BeginRename()
    {
        titleBox.Text = title;
        titleBox.Visible = true;
        titleBox.Focus();
        titleBox.SelectAll();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        if (!titleBox.Visible)
            DrawLabel(e.Graphics, title, titleBox.Font, titleRect, Theme.Fg, false);
    }

    // ---------- persistence ----------
    // When the file changes underneath an open window (something was dropped on the folder's
    // Desktop icon), the window's next save keeps those new items instead of overwriting them.
    DateTime loadedStamp;
    readonly HashSet<string> removedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    static string[] ConfigLines(string file)
    {
        return ReadFolderFileConfig(file).Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries);
    }

    void MergeChangesFromFile()
    {
        if (filePath == null || !File.Exists(filePath)) return;
        DateTime now = File.GetLastWriteTimeUtc(filePath);
        if (now == loadedStamp) return;
        foreach (var line in ConfigLines(filePath))
        {
            if (!line.StartsWith("item=")) continue;
            var it = ParseItem(line.Substring(5));
            if (removedPaths.Contains(it.Path) || items.Exists(x => string.Equals(x.Path, it.Path, StringComparison.OrdinalIgnoreCase))) continue;
            it.Icon = Native.GetIcon(it.Path);
            items.Add(it);
        }
    }

    // item=<path>|<label>|#<file id>   (the id part is optional; older folders don't have it)
    static Item ParseItem(string v)
    {
        string itemId = null;
        int hash = v.LastIndexOf("|#", StringComparison.Ordinal);
        if (hash >= 0) { itemId = v.Substring(hash + 2); v = v.Substring(0, hash); }
        int bar = v.LastIndexOf('|');
        string p = bar >= 0 ? v.Substring(0, bar) : v;
        return new Item { Path = p, Label = bar >= 0 ? v.Substring(bar + 1) : DefaultLabel(p), Id = itemId };
    }

    static string ItemLine(Item it)
    {
        return "item=" + it.Path + "|" + it.Label.Replace("|", "/") + (string.IsNullOrEmpty(it.Id) ? "" : "|#" + it.Id);
    }

    // If an item's file or folder has moved, follow it (true if its path changed). Also notes the
    // ID of items that don't have one yet, so they can be followed later.
    static readonly Dictionary<string, DateTime> notFoundAt = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);

    static bool Track(Item it)
    {
        if (Native.IsUrl(it.Path)) return false;
        if (File.Exists(it.Path) || Directory.Exists(it.Path))
        {
            if (it.Id != null) return false;
            it.Id = FileTracker.IdOf(it.Path);
            return it.Id != null;
        }
        // Searching for a missing item walks every drive and several user folders: slow. When it
        // turns up nothing, don't search again for a while, or every open of a folder holding a
        // deleted item (and the helper's start-up warm-up) pays for it again.
        DateTime last;
        if (notFoundAt.TryGetValue(it.Path, out last) && (DateTime.Now - last).TotalMinutes < 2) return false;
        string now = FileTracker.Find(it.Id) ?? FileTracker.FindByName(it.Path);
        if (now == null) { notFoundAt[it.Path] = DateTime.Now; return false; }
        notFoundAt.Remove(it.Path);
        HoverWatcher.Log("followed a moved item: " + it.Path + " -> " + now);
        it.Path = now;
        it.Id = FileTracker.IdOf(now);
        return true;
    }

    void LoadConfig()
    {
        string[] lines;
        if (filePath != null)
        {
            title = Path.GetFileNameWithoutExtension(filePath);
            lines = ConfigLines(filePath);
            try { loadedStamp = File.GetLastWriteTimeUtc(filePath); } catch { }
        }
        else
        {
            if (!File.Exists(cfgPath)) { title = id == "default" ? "My Folder" : id; return; }
            lines = File.ReadAllLines(cfgPath, Encoding.UTF8);
        }
        bool changed = false;
        foreach (var line in lines)
        {
            int eq = line.IndexOf('=');
            if (eq < 0) continue;
            string k = line.Substring(0, eq), v = line.Substring(eq + 1);
            if (k == "title") { if (filePath == null) title = v; }
            else if (k == "item")
            {
                var it = ParseItem(v);
                if (Track(it)) changed = true;   // moved since last time? follow it
                it.Icon = Native.GetIcon(it.Path);
                items.Add(it);
            }
        }
        if (changed) Save();   // remember the new places
    }

    void Save()
    {
        MergeChangesFromFile();
        var sb = new StringBuilder();
        sb.AppendLine("title=" + title);
        sb.AppendLine("iconlook=" + Theme.LookSig);   // which look the icon was drawn in (see RefreshAllIcons)
        foreach (var it in items) sb.AppendLine(ItemLine(it));
        if (filePath != null)
        {
            try
            {
                WriteFolderFile(filePath, RenderPreview(items, 256), sb.ToString());
                loadedStamp = File.GetLastWriteTimeUtc(filePath);
                Native.NotifyFileChanged(filePath);
            }
            catch (Exception ex) { MessageBox.Show(this, "Couldn't save folder:\n" + ex.Message, Installer.AppName);}
            return;
        }
        File.WriteAllText(cfgPath, sb.ToString(), Encoding.UTF8);
        SavePreviewIcon();
    }

    // A .appfolder file is a real .ico (so Explorer shows the live mini-grid as its icon)
    // followed by a marker and the folder's settings as text.
    static readonly byte[] Marker = Encoding.ASCII.GetBytes("\r\n#APPFOLDER-CONFIG\r\n");

    public static void WriteFolderFile(string path, Bitmap preview, string cfg)
    {
        byte[] ico = IcoBytes(preview);
        byte[] text = new UTF8Encoding(false).GetBytes(cfg);
        string tmp = path + ".tmp";
        using (var fs = File.Create(tmp))
        {
            fs.Write(ico, 0, ico.Length);
            fs.Write(Marker, 0, Marker.Length);
            fs.Write(text, 0, text.Length);
        }
        if (File.Exists(path)) File.Replace(tmp, path, null); else File.Move(tmp, path);
    }

    static string ReadFolderFileConfig(string path)
    {
        try
        {
            byte[] b = File.ReadAllBytes(path);
            for (int i = b.Length - Marker.Length; i >= 0; i--)
            {
                int j = 0;
                while (j < Marker.Length && b[i + j] == Marker[j]) j++;
                if (j == Marker.Length)
                {
                    int start = i + Marker.Length;
                    return Encoding.UTF8.GetString(b, start, b.Length - start);
                }
            }
        }
        catch { }
        return "";
    }

    static string DefaultLabel(string p)
    {
        if (Native.IsUrl(p))
        {
            try { return new Uri(p).Host.Replace("www.", ""); } catch { return p; }
        }
        string n = Path.GetFileName(p.TrimEnd('\\'));
        if (string.IsNullOrEmpty(n)) return p;
        string ext = Path.GetExtension(n).ToLowerInvariant();
        if (ext == ".lnk" || ext == ".exe" || ext == ".url" || ext == ".appref-ms") n = Path.GetFileNameWithoutExtension(n);
        return n;
    }

    // ---------- UI ----------
    void Rebuild()
    {
        Text = title;
        if (!titleBox.Focused) titleBox.Text = title;
        grid.SuspendLayout();
        foreach (Control c in grid.Controls) c.Dispose();
        grid.Controls.Clear();

        int n = items.Count + 1;
        for (int i = 0; i < n; i++)
        {
            var t = new Tile { Item = i < items.Count ? items[i] : null, Font = Font, Size = new Size(TileW, TileH) };
            t.Location = new Point((i % Cols) * TileW, (i / Cols) * TileH);
            WireTile(t);
            grid.Controls.Add(t);
        }
        grid.ResumeLayout();

        int rows = Math.Max(1, (n + Cols - 1) / Cols);
        int visRows = Math.Min(rows, MaxRows);
        int pad = Px(14), head = Px(56), btn = Px(28);
        int scrollW = rows > MaxRows ? SystemInformation.VerticalScrollBarWidth : 0;
        grid.SetBounds(pad, head, Cols * TileW + scrollW, visRows * TileH);
        // header: name on the left (click to rename), settings gear and close X on the right
        closeBtn.SetBounds(grid.Right - btn - Px(4), Px(16), btn, btn);
        gearBtn.SetBounds(closeBtn.Left - btn - Px(8), Px(16), btn, btn);
        int th = titleBox.PreferredHeight;
        titleBox.SetBounds(pad + Px(8), Px(16) + (btn - th) / 2, gearBtn.Left - pad - Px(18), th);
        titleRect = new Rectangle(titleBox.Left, Px(16), titleBox.Width, btn);
        ClientSize = new Size(grid.Right + pad, grid.Bottom + pad);
        Invalidate();
        RequestDecos();
        SetFormIcon();
    }

    void WireTile(Tile t)
    {
        t.MouseDown += (s, e) => { if (e.Button == MouseButtons.Left) PressTile(t); };
        t.MouseMove += (s, e) => { if (e.Button == MouseButtons.Left) DragTileMove(); };
        t.MouseUp += (s, e) =>
        {
            if (e.Button == MouseButtons.Left)
            {
                if (EndTileDrag()) return;   // that was a reorder, not a click
                if (t.Item == null) ShowAddMenu(); else Launch(t.Item);
            }
            else if (e.Button == MouseButtons.Right)
            {
                if (t.Item == null) ShowBackgroundMenu();
                else ShowTileMenu(t);
            }
        };
        t.DragEnter += (s, e) => { OnDragOverAny(e); SetDropTile(t); };
        t.DragOver += (s, e) => OnDragOverAny(e);
        t.DragLeave += (s, e) => SetDropTile(null);
        t.DragDrop += (s, e) => { SetDropTile(null); OnDropAny(e, t); };
    }

    // ---------- reordering (phone-style) ----------
    // Press an app and drag: it lifts and follows the pointer, the others slide into their new
    // places as you go, and it settles into its spot when you let go.
    Tile pressTile, dragTile;
    Point pressScreen, grabOffset;
    readonly Dictionary<Tile, Point> slideTo = new Dictionary<Tile, Point>();
    readonly System.Windows.Forms.Timer slideTimer = new System.Windows.Forms.Timer { Interval = 10 };

    void PressTile(Tile t) { pressTile = t; pressScreen = Cursor.Position; }

    // where the tile for list position i sits in the grid (taking scrolling into account)
    Point SlotOf(int i) { return new Point((i % Cols) * TileW + grid.AutoScrollPosition.X, (i / Cols) * TileH + grid.AutoScrollPosition.Y); }

    void DragTileMove()
    {
        if (pressTile == null || pressTile.IsDisposed || pressTile.Item == null) return;
        Point p = Cursor.Position;
        if (dragTile == null)
        {
            // a click with a little wobble is still a click: start a reorder only after a clear move
            int slop = Math.Max(SystemInformation.DragSize.Width, Px(10));
            if (Math.Abs(p.X - pressScreen.X) <= slop && Math.Abs(p.Y - pressScreen.Y) <= slop) return;
            // lift it
            dragTile = pressTile;
            var ts = grid.PointToScreen(dragTile.Location);
            grabOffset = new Point(pressScreen.X - ts.X, pressScreen.Y - ts.Y);
            dragTile.Lifted = true;
            dragTile.BringToFront();
            dragTile.Invalidate();
            Interacted = true;
        }
        // follow the pointer (kept inside the grid)
        var gp = grid.PointToClient(new Point(p.X - grabOffset.X, p.Y - grabOffset.Y));
        gp.X = Math.Max(-TileW / 3, Math.Min(gp.X, grid.ClientSize.Width - TileW * 2 / 3));
        gp.Y = Math.Max(-TileH / 3, Math.Min(gp.Y, grid.ClientSize.Height - TileH * 2 / 3));
        dragTile.Location = gp;

        // which spot is it over? (by its centre)
        int cx = gp.X - grid.AutoScrollPosition.X + TileW / 2, cy = gp.Y - grid.AutoScrollPosition.Y + TileH / 2;
        int col = Math.Max(0, Math.Min(Cols - 1, cx / TileW)), row = Math.Max(0, cy / TileH);
        int to = Math.Min(row * Cols + col, items.Count - 1), from = items.IndexOf(dragTile.Item);
        if (to != from && to >= 0 && from >= 0)
        {
            items.RemoveAt(from);
            items.Insert(to, dragTile.Item);
            SlideOthers();
        }
    }

    // animate every tile except the one being dragged towards its spot
    void SlideOthers()
    {
        foreach (Control c in grid.Controls)
        {
            var t = c as Tile;
            if (t == null || t == dragTile) continue;
            int i = t.Item == null ? items.Count : items.IndexOf(t.Item);
            if (i >= 0) slideTo[t] = SlotOf(i);
        }
        StartSlide();
    }

    void StartSlide()
    {
        if (slideTimer.Enabled) return;
        slideTimer.Start();
    }

    void SlideTick()
    {
        bool moving = false;
        foreach (var kv in new List<KeyValuePair<Tile, Point>>(slideTo))
        {
            var t = kv.Key;
            if (t.IsDisposed) { slideTo.Remove(t); continue; }
            Point a = t.Location, b = kv.Value;
            int dx = b.X - a.X, dy = b.Y - a.Y;
            if (Math.Abs(dx) <= 1 && Math.Abs(dy) <= 1) { t.Location = b; slideTo.Remove(t); continue; }
            t.Location = new Point(a.X + (int)Math.Round(dx * 0.32), a.Y + (int)Math.Round(dy * 0.32));   // ease towards it
            moving = true;
        }
        if (!moving) slideTimer.Stop();
    }

    // Let go: true if a tile was being dragged (so it isn't also treated as a click).
    bool EndTileDrag()
    {
        var t = dragTile;
        pressTile = null;
        if (t == null) return false;
        dragTile = null;
        if (!t.IsDisposed)
        {
            t.Lifted = false;
            t.Invalidate();
            slideTo[t] = SlotOf(items.IndexOf(t.Item));   // settle into its spot
            StartSlide();
        }
        Save();
        SetFormIcon();
        return true;
    }

    void SetDropTile(Tile t)
    {
        if (dropTile == t) return;
        if (dropTile != null && !dropTile.IsDisposed) { dropTile.DropTarget = false; dropTile.Invalidate(); }
        dropTile = t;
        if (t != null) { t.DropTarget = true; t.Invalidate(); }
    }

    void BuildMenus() { }   // menus are built fresh each time they open (ShowBackgroundMenu / ShowTileMenu)

    // Right-click on empty space (or the + tile)
    void ShowBackgroundMenu()
    {
        var m = new PrismMenu(this);
        m.Add("Add a website...", '', AddWebsite);
        if (filePath == null)
            m.Add("Put a shortcut on the Desktop", '', () => CreateDesktopShortcut(id, title));
        m.Add("Create another folder...", '', NewFolder);
        m.AddSeparator();
        m.Add("Close", '', Close, true);
        ShowMenu(m);
    }

    // The + tile: what to add
    void ShowAddMenu()
    {
        var m = new PrismMenu(this);
        m.Add("Apps or files...", '', AddFiles);
        m.Add("A folder...", '', AddFolder);
        m.Add("A website...", '', AddWebsite);
        ShowMenu(m);
    }

    void AddWebsite()
    {
        string url = Prompt("Add a website", "Address (for example youtube.com)", "");
        if (string.IsNullOrEmpty(url)) return;
        if (!url.Contains("://")) url = "https://" + url;
        Uri u;
        if (!Uri.TryCreate(url, UriKind.Absolute, out u) || string.IsNullOrEmpty(u.Host) || !u.Host.Contains("."))
        {
            ShowNote("That doesn't look like a web address.");
            return;
        }
        AddPaths(new[] { u.ToString() }, -1);
    }

    // A small message in Prysm's style (instead of a Windows message box)
    void ShowNote(string text)
    {
        var m = new PrismMenu(this);
        m.Add(text, '', null);
        ShowMenu(m);
    }

    // Right-click on an app or file in the folder
    void ShowTileMenu(Tile t)
    {
        var it = t.Item;
        var m = new PrismMenu(this);
        m.Add("Open", '', () => Launch(it));
        if (!Native.IsUrl(it.Path))
        {
            m.Add("Run as administrator", '', () => Launch(it, true));
            m.Add("Open file location", '', () => OpenLocation(it));
        }
        m.AddSeparator();
        m.Add("Rename", '', () =>
        {
            string n = Prompt("Rename", "Name shown under the icon", it.Label);
            if (!string.IsNullOrEmpty(n)) { it.Label = n; Save(); Rebuild(); }
        });
        int i = items.IndexOf(it);
        if (i > 0) m.Add("Move left", '', () => MoveItem(it, -1));
        if (i >= 0 && i < items.Count - 1) m.Add("Move right", '', () => MoveItem(it, 1));
        m.AddSeparator();
        m.Add("Remove from folder", '', () => { items.Remove(it); removedPaths.Add(it.Path); Save(); Rebuild(); }, true);
        ShowMenu(m);
    }

    // Opens a menu at the pointer; what you pick runs once the menu has gone (so a rename box
    // or file picker it opens isn't mistaken for clicking away).
    void ShowMenu(PrismMenu m)
    {
        busy = true;
        m.Measure();
        m.PlaceAt(Cursor.Position);
        m.FormClosed += (s, e) =>
        {
            busy = false;
            if (IsDisposed) return;
            var act = m.Chosen;
            if (act != null) BeginInvoke(act);
            else BeginInvoke((Action)CloseIfClickedAway);
        };
        m.Show(this);
    }

    // The gear: Prysm's settings card, just below it, right-aligned with it.
    public void TestShowSettings() { ShowSettings(); }   // for the edge self-test

    // ---- for the demo GIF: pose states and find where things are (client coordinates) ----
    public void PromoHover(int tileIndex)
    {
        int i = 0;
        foreach (Control c in grid.Controls)
        {
            var t = c as Tile;
            if (t == null) continue;
            int idx = t.Item == null ? items.Count : items.IndexOf(t.Item);
            bool on = idx == tileIndex;
            if (t.Hover != on) { t.Hover = on; t.Invalidate(); }
            i++;
        }
        closeBtn.SetHover(tileIndex == -2);
        gearBtn.SetHover(tileIndex == -3);
        Refresh();
    }
    public Rectangle PromoTileRect(int i) { var p = SlotOf(i); return new Rectangle(grid.Left + p.X, grid.Top + p.Y, TileW, TileH); }
    public int PromoTileCount { get { return items.Count + 1; } }
    public Rectangle PromoGearRect { get { return gearBtn.Bounds; } }
    public Rectangle PromoCloseRect { get { return closeBtn.Bounds; } }

    // The gear toggles: click it again while the card is open and the card closes.
    // (Pressing the gear already takes focus from the card, which starts it closing, so a card
    // that began closing a moment ago counts as "was open" too.)
    SettingsCard settingsCard;
    DateTime settingsClosedAt = DateTime.MinValue;

    void ShowSettings()
    {
        if (settingsCard != null && !settingsCard.IsDisposed) { settingsCard.Close(); return; }
        if ((DateTime.Now - settingsClosedAt).TotalMilliseconds < 400) return;
        busy = true;
        var card = new SettingsCard(this, SetLook);
        settingsCard = card;
        card.FormClosing += (s, e) => { settingsClosedAt = DateTime.Now; };
        var gr = gearBtn.RectangleToScreen(gearBtn.ClientRectangle);
        var close = closeBtn.RectangleToScreen(closeBtn.ClientRectangle);
        var wa = Screen.FromPoint(gr.Location).WorkingArea;
        int x = Math.Min(close.Right - card.Width, wa.Right - card.Width - 8);
        int y = gr.Bottom + Px(8);
        if (y + card.Height > wa.Bottom - 8) y = gr.Top - card.Height - Px(8);
        card.Location = new Point(Math.Max(wa.Left + 8, x), y);
        card.FormClosed += (s, e) =>
        {
            busy = false;
            settingsClosedAt = DateTime.Now;
            if (settingsCard == card) settingsCard = null;
            if (!IsDisposed) BeginInvoke((Action)CloseIfClickedAway);   // clicked away from both: close the folder too
        };
        card.Show(this);
        card.Activate();
    }

    // Switching look rebuilds the window, so reopen this folder right where it is.
    void SetLook(bool glass)
    {
        if (glass == Theme.Glass) return;
        Settings.Glass = glass;
        Reopen();
    }

    // Corners: Rounded or Sharper (the folder, its menus and cards, and the Desktop icons)
    public void SetCorners(bool sharp)
    {
        if (sharp == Theme.Sharp) return;
        Settings.SharpCorners = sharp;
        Reopen();
    }

    void Reopen()
    {
        string target = filePath != null ? "\"" + filePath + "\"" : "\"" + id + "\"";
        // the new window also redraws the Desktop icons in the new look
        try { Process.Start(Application.ExecutablePath, target + " --at " + Left + "," + Top + " --refresh-icons"); } catch { }
        Close();
    }

    void MoveItem(Item it, int delta)
    {
        int i = items.IndexOf(it), j = i + delta;
        if (i < 0 || j < 0 || j >= items.Count) return;
        items.RemoveAt(i); items.Insert(j, it);
        Save(); Rebuild();
    }

    // ---------- adding ----------
    void AddFiles()
    {
        using (var d = new OpenFileDialog { Multiselect = true, Title = "Add apps or files", DereferenceLinks = false, Filter = "All files|*.*|Programs and shortcuts|*.exe;*.lnk;*.url;*.bat;*.cmd" })
        {
            string start = Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu);
            d.InitialDirectory = Path.Combine(start, "Programs");
            busy = true;
            var r = d.ShowDialog(this);
            busy = false; Activate();
            if (r == DialogResult.OK) AddPaths(d.FileNames, -1);
        }
    }

    void AddFolder()
    {
        busy = true;
        var picked = FolderPicker.Pick(Handle, "Add folders");
        busy = false;
        if (!IsDisposed) Activate();
        if (picked.Count > 0) AddPaths(picked, -1);
    }

    // Is this path this folder's own file? (Web addresses aren't file paths: never.)
    bool IsThisFolder(string p)
    {
        if (filePath == null || Native.IsUrl(p)) return false;
        try { return string.Equals(Path.GetFullPath(p), filePath, StringComparison.OrdinalIgnoreCase); }
        catch { return false; }
    }

    // Files dropped on this folder's icon on the Desktop.
    public void AddDropped(IEnumerable<string> paths) { AddPaths(paths, -1); }

    void AddPaths(IEnumerable<string> paths, int at)
    {
        foreach (var p in paths)
        {
            if (IsThisFolder(p)) continue;   // not itself
            removedPaths.Remove(p);
            if (items.Exists(x => string.Equals(x.Path, p, StringComparison.OrdinalIgnoreCase))) continue;
            var it = new Item { Path = p, Label = DefaultLabel(p), Icon = Native.GetIcon(p), Id = FileTracker.IdOf(p) };
            if (at >= 0 && at <= items.Count) items.Insert(at++, it); else items.Add(it);
        }
        Save(); Rebuild();
    }

    void OnDragOverAny(DragEventArgs e)
    {
        if (e.Data.GetDataPresent(typeof(Tile))) e.Effect = DragDropEffects.Move;
        else if (e.Data.GetDataPresent(DataFormats.FileDrop)) e.Effect = DragDropEffects.Copy | DragDropEffects.Link;
        else if (e.Data.GetDataPresent(DataFormats.UnicodeText)) e.Effect = DragDropEffects.Copy | DragDropEffects.Link;
        else e.Effect = DragDropEffects.None;
    }

    void OnDropAny(DragEventArgs e, Tile target)
    {
        Interacted = true;   // you just dropped something in: stay open to show it
        int at = target != null && target.Item != null ? items.IndexOf(target.Item) : -1;
        if (e.Data.GetDataPresent(typeof(Tile)))
        {
            var src = (Tile)e.Data.GetData(typeof(Tile));
            if (src.Item == null || (target != null && src.Item == target.Item)) return;
            items.Remove(src.Item);
            at = target != null && target.Item != null ? items.IndexOf(target.Item) : items.Count;
            items.Insert(at, src.Item);
            Save();
            BeginInvoke((Action)Rebuild);   // rebuild after DoDragDrop returns
        }
        else if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            AddPaths((string[])e.Data.GetData(DataFormats.FileDrop), at);
        }
        else if (e.Data.GetDataPresent(DataFormats.UnicodeText))
        {
            string t = ((string)e.Data.GetData(DataFormats.UnicodeText)).Trim();
            if (Native.IsUrl(t)) AddPaths(new[] { t }, at);
        }
    }

    // ---------- launching ----------
    [DllImport("user32.dll")] static extern bool AllowSetForegroundWindow(int processId);

    // "Open file location": File Explorer with the item selected (in front, like opening an app)
    void OpenLocation(Item it)
    {
        try
        {
            if (!File.Exists(it.Path) && !Directory.Exists(it.Path) && Track(it)) { it.Icon = Native.GetIcon(it.Path); Save(); }
            AllowSetForegroundWindow(-1 /*ASFW_ANY*/);
            Process.Start(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe"), "/select,\"" + it.Path + "\"");
            Close();
        }
        catch (Exception ex) { ShowNote("Couldn't open its location: " + ex.Message); }
    }

    void Launch(Item it, bool admin = false)
    {
        if (!Native.IsUrl(it.Path) && !File.Exists(it.Path) && !Directory.Exists(it.Path))
        {
            // moved while this folder was open? follow it; otherwise it's really gone
            if (Track(it)) { it.Icon = Native.GetIcon(it.Path); Save(); }
            else { ShowNote("\"" + it.Label + "\" has been moved to another drive or deleted."); return; }
        }
        try
        {
            // Let whatever we open come to the front. Without this Windows keeps it behind other
            // windows (it wasn't the app you clicked in) - a folder's File Explorer window
            // especially, which then looks like nothing happened.
            AllowSetForegroundWindow(-1 /*ASFW_ANY*/);
            ProcessStartInfo psi;
            if (!admin && !Native.IsUrl(it.Path) && Directory.Exists(it.Path))
                psi = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe"), "\"" + it.Path + "\"");
            else
            {
                psi = new ProcessStartInfo(it.Path) { UseShellExecute = true };
                if (!Native.IsUrl(it.Path))
                {
                    string dir = Directory.Exists(it.Path) ? it.Path : Path.GetDirectoryName(it.Path);
                    if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir)) psi.WorkingDirectory = dir;
                }
                if (admin) psi.Verb = "runas";
            }
            Process.Start(psi);
            Close();
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            if (ex.NativeErrorCode != 1223)   // 1223 = user cancelled UAC
                MessageBox.Show(this, "Couldn't open \"" + it.Label + "\":\n" + ex.Message, Installer.AppName);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Couldn't open \"" + it.Label + "\":\n" + ex.Message, Installer.AppName);
        }
    }

    // ---------- folder management ----------
    void CommitTitle()
    {
        string t = titleBox.Text.Trim();
        if (t.Length == 0) { titleBox.Text = title; return; }
        if (t == title) return;
        if (filePath != null)
        {
            // The folder's name is its file name, so renaming it renames the file.
            string newPath = Path.Combine(Path.GetDirectoryName(filePath), SafeFileName(t) + Installer.Ext);
            try
            {
                if (!string.Equals(newPath, filePath, StringComparison.OrdinalIgnoreCase) && File.Exists(newPath))
                    throw new IOException("Something named \"" + Path.GetFileName(newPath) + "\" is already there.");
                File.Move(filePath, newPath);
                filePath = newPath;
                t = Path.GetFileNameWithoutExtension(newPath);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Couldn't rename:\n" + ex.Message, Installer.AppName);
                titleBox.Text = title; return;
            }
            title = t; Text = t; titleBox.Text = t;
            return;
        }
        title = t; Text = t; Save();
    }

    void NewFolder()
    {
        string name = Prompt("New folder", "Folder name:", "New Folder");
        if (string.IsNullOrEmpty(name)) return;
        if (Installer.IsInstalled)
        {
            string f = Installer.CreateFolderFile(null, name);
            Process.Start(Application.ExecutablePath, "\"" + f + "\"");
            Close();
            return;
        }
        string newId = SafeId(name);
        string p = Path.Combine(dataDir, newId + ".txt");
        int k = 2;
        while (File.Exists(p)) { newId = SafeId(name) + "_" + k++; p = Path.Combine(dataDir, newId + ".txt"); }
        File.WriteAllText(p, "title=" + name + "\r\npinned=0\r\n", Encoding.UTF8);
        CreateDesktopShortcut(newId, name);
        Process.Start(Application.ExecutablePath, "\"" + newId + "\"");
        Close();
    }

    static string SafeId(string s)
    {
        var sb = new StringBuilder();
        foreach (char c in s) sb.Append(char.IsLetterOrDigit(c) || c == '-' || c == '_' ? c : '_');
        return sb.Length == 0 ? "folder" : sb.ToString();
    }

    void CreateDesktopShortcut(string folderId, string name)
    {
        try
        {
            string desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            string lnkPath = Path.Combine(desktop, SafeFileName(name) + ".lnk");
            string icoPath = Path.Combine(dataDir, folderId + ".ico");
            if (!File.Exists(icoPath)) WriteIco(icoPath, RenderPreview(new List<Item>(), 256));
            Type t = Type.GetTypeFromProgID("WScript.Shell");
            object sh = Activator.CreateInstance(t);
            object lnk = t.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, sh, new object[] { lnkPath });
            Type lt = lnk.GetType();
            lt.InvokeMember("TargetPath", BindingFlags.SetProperty, null, lnk, new object[] { Application.ExecutablePath });
            lt.InvokeMember("Arguments", BindingFlags.SetProperty, null, lnk, new object[] { "\"" + folderId + "\"" });
            lt.InvokeMember("WorkingDirectory", BindingFlags.SetProperty, null, lnk, new object[] { Path.GetDirectoryName(Application.ExecutablePath) });
            lt.InvokeMember("IconLocation", BindingFlags.SetProperty, null, lnk, new object[] { icoPath + ",0" });
            lt.InvokeMember("Description", BindingFlags.SetProperty, null, lnk, new object[] { Installer.AppName + ": " + name });
            lt.InvokeMember("Save", BindingFlags.InvokeMethod, null, lnk, null);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Couldn't create shortcut:\n" + ex.Message, Installer.AppName);
        }
    }

    public static string SafeFileName(string s)
    {
        foreach (char c in Path.GetInvalidFileNameChars()) s = s.Replace(c, '_');
        return s;
    }

    // ---------- folder icon (2x2 / 3x3 mini grid like a phone) ----------
    public static Bitmap RenderPreview(List<Item> list, int size)
    {
        var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            float m = size * 0.04f;
            var box = new RectangleF(m, m, size - 2 * m, size - 2 * m);
            float line = Math.Max(1f, size / 96f);
            // The frame matches the folder's look: See-through is a translucent Prism glass tile
            // (the wallpaper shows through), Solid is the Prism panel; light or dark with Windows.
            using (var p = Rounded(box, size * (Theme.Sharp ? 0.10f : 0.22f)))
            {
                Color panel = Theme.Light ? Color.FromArgb(0xf3, 0xf3, 0xf5) : Theme.Glass ? Color.FromArgb(0x14, 0x14, 0x16) : Color.FromArgb(0x20, 0x20, 0x22);
                int body = Theme.Glass ? (Theme.Light ? 150 : 135) : 250;
                using (var b = new SolidBrush(Color.FromArgb(body, panel))) g.FillPath(b, p);
                g.SetClip(p);
                // Prism's sheen: a breath of white, brighter at the top
                int top = Theme.Light ? (Theme.Glass ? 120 : 70) : (Theme.Glass ? 46 : 20), bottom = Theme.Light ? 20 : (Theme.Glass ? 10 : 4);
                using (var sheen = new LinearGradientBrush(box, Color.FromArgb(top, 255, 255, 255), Color.FromArgb(bottom, 255, 255, 255), 90f))
                    g.FillRectangle(sheen, box);
                g.ResetClip();
                Color rim = Theme.Light ? (Theme.Glass ? Color.FromArgb(210, 255, 255, 255) : Color.FromArgb(40, 0, 0, 0))
                                        : (Theme.Glass ? Color.FromArgb(70, 255, 255, 255) : Color.FromArgb(38, 255, 255, 255));
                using (var pen = new Pen(rim, line)) g.DrawPath(pen, p);
                if (Theme.Glass && Theme.Light)   // a faint outer edge so a light glass tile still shows on a light wallpaper
                    using (var pen = new Pen(Color.FromArgb(28, 0, 0, 0), line)) g.DrawPath(pen, Rounded(RectangleF.Inflate(box, line, line), size * (Theme.Sharp ? 0.10f : 0.22f) + line));
            }
            Color slot = Theme.Light ? Color.FromArgb(22, 0, 0, 0) : Color.FromArgb(30, 255, 255, 255);
            int n = Theme.Grid3 && list.Count > 4 ? 3 : 2;   // 2x2, or 3x3 for bigger folders if that's the setting
            float inner = size - 2 * m, cell = inner / (n + 0.6f), gap = (inner - n * cell) / (n + 1);
            for (int i = 0; i < n * n; i++)
            {
                float x = m + gap + (i % n) * (cell + gap), y = m + gap + (i / n) * (cell + gap);
                var r = new RectangleF(x, y, cell, cell);
                if (i < list.Count && list[i].Icon != null) g.DrawImage(list[i].Icon, r);
                else using (var b = new SolidBrush(slot))
                     using (var p = Rounded(r, cell * 0.25f)) g.FillPath(b, p);
            }
        }
        return bmp;
    }

    // Redraws every Desktop folder's icon (and the New-menu template) in the current look:
    // after switching See-through/Solid, or Windows switching light/dark.
    public static void RefreshAllIcons()
    {
        string want = "iconlook=" + Theme.LookSig;
        foreach (var dir in new[] { Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                                    Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory) })
        {
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) continue;
            string[] files;
            try { files = Directory.GetFiles(dir, "*" + Installer.Ext); } catch { continue; }
            foreach (var f in files)
            {
                try
                {
                    if (Array.IndexOf(ConfigLines(f), want) >= 0) continue;   // already in this look
                    using (var ff = new FolderForm("refresh", f)) ff.Save();
                }
                catch { }
            }
        }
        try { Installer.WriteTemplate(); } catch { }
    }

    static void Glow(Graphics g, PointF c, float radius, Color col)
    {
        using (var path = new GraphicsPath())
        {
            path.AddEllipse(c.X - radius, c.Y - radius, radius * 2, radius * 2);
            using (var b = new PathGradientBrush(path) { CenterColor = col, SurroundColors = new[] { Color.FromArgb(0, col) } })
                g.FillPath(b, path);
        }
    }

    void SavePreviewIcon()
    {
        try
        {
            WriteIco(Path.Combine(dataDir, id + ".ico"), RenderPreview(items, 256));
            Native.SHChangeNotify(0x08000000, 0, IntPtr.Zero, IntPtr.Zero);
        }
        catch { }
    }

    public static void WriteIco(string path, Bitmap bmp)
    {
        File.WriteAllBytes(path, IcoBytes(bmp));
    }

    // Multi-size .ico (256/48/32/16, PNG-compressed) from a 256px image. Disposes bmp.
    static byte[] IcoBytes(Bitmap bmp)
    {
        int[] sizes = { 256, 48, 32, 16 };
        var pngs = new List<byte[]>();
        foreach (int sz in sizes)
        {
            using (var scaled = new Bitmap(sz, sz, PixelFormat.Format32bppArgb))
            {
                using (var g = Graphics.FromImage(scaled))
                {
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    g.DrawImage(bmp, 0, 0, sz, sz);
                }
                using (var ms = new MemoryStream()) { scaled.Save(ms, ImageFormat.Png); pngs.Add(ms.ToArray()); }
            }
        }
        bmp.Dispose();
        using (var ms = new MemoryStream())
        using (var w = new BinaryWriter(ms))
        {
            w.Write((short)0); w.Write((short)1); w.Write((short)sizes.Length);
            int offset = 6 + 16 * sizes.Length;
            for (int i = 0; i < sizes.Length; i++)
            {
                byte d = (byte)(sizes[i] >= 256 ? 0 : sizes[i]);
                w.Write(d); w.Write(d); w.Write((byte)0); w.Write((byte)0);
                w.Write((short)1); w.Write((short)32); w.Write(pngs[i].Length); w.Write(offset);
                offset += pngs[i].Length;
            }
            foreach (var p in pngs) w.Write(p);
            w.Flush();
            return ms.ToArray();
        }
    }

    // The window's own icon. Icon.FromHandle doesn't own the handle, so copy it into an icon that
    // does and free the original: otherwise every folder opened leaked an icon handle, and the hover
    // helper (which runs all day) could eventually run out and stop drawing.
    void SetFormIcon()
    {
        Icon made;
        using (var big = RenderPreview(items, 64))
        using (var small = new Bitmap(big, 32, 32))
        {
            IntPtr h = small.GetHicon();
            try { using (var tmp = System.Drawing.Icon.FromHandle(h)) made = (Icon)tmp.Clone(); }
            finally { Native.DestroyIcon(h); }
        }
        var old = Icon;
        Icon = made;
        if (old != null && old != made && ownsIcon) old.Dispose();
        ownsIcon = true;
    }
    bool ownsIcon;

    // a rounded window region (only used where Windows can't round the corners itself)
    [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr h);
    Region RoundRegion()
    {
        IntPtr h = Native.CreateRoundRectRgn(0, 0, Width + 1, Height + 1, Px(20), Px(20));
        try { return System.Drawing.Region.FromHrgn(h); } finally { DeleteObject(h); }
    }

    // ---------- window behaviour ----------
    void PositionNearCursor()
    {
        var c = Cursor.Position;
        var wa = Screen.FromPoint(c).WorkingArea;
        int x = Math.Max(wa.Left + 8, Math.Min(c.X - Width / 2, wa.Right - Width - 8));
        int y = Math.Max(wa.Top + 8, Math.Min(c.Y - Height / 2, wa.Bottom - Height - 8));
        Location = new Point(x, y);
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        int round = 2; // DWMWCP_ROUND (Windows 11)
        bool ok = false;
        try { ok = Native.DwmSetWindowAttribute(Handle, 33, ref round, 4) == 0; } catch { }
        if (!ok) Region = RoundRegion();
        try
        {
            int dark = Theme.Light ? 0 : 1, off = 1, rim = Theme.ToBgr(Theme.Rim);
            Native.DwmSetWindowAttribute(Handle, 20, ref dark, 4);   // dark-mode frame
            Native.DwmSetWindowAttribute(Handle, 3, ref off, 4);     // no open/close animation: closes instantly
            Native.DwmSetWindowAttribute(Handle, 34, ref rim, 4);    // hairline rim like Prism's cards
        }
        catch { }
    }

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            // (no drop shadow: the card layer behind draws the shape)
            return cp;
        }
    }

    public static bool StartRenaming;   // open with the name selected, ready to type

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        if (!SpringMode)
        {
            Activate();
            if (StartRenaming) BeginRename();
            else grid.Focus();
        }
        BeginInvoke((Action)(() => ready = true));
        AnimateIn();
    }

    // ---------- open animation ----------
    // The glass springs in (GlassBackdrop.AnimateIn); the icons and text fade in with it. On the
    // Solid look there's no glass, so the window also rises a few pixels into place as it fades in.
    readonly System.Windows.Forms.Timer animTimer = new System.Windows.Forms.Timer { Interval = 8 };
    readonly Stopwatch animClock = new Stopwatch();
    Point animTarget;
    const double FadeMs = 170, RiseMs = 230;

    // ---------- close animation ----------
    // Any close (X, Esc, clicking away, launching an app) first plays a quick fade-and-shrink,
    // then really closes. Windows shutting down or the app exiting closes straight away.
    bool closeAnimDone, closing;
    const double CloseMs = 120;

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        base.OnFormClosing(e);
        if (e.Cancel || closeAnimDone || !Visible) return;
        if (e.CloseReason == CloseReason.WindowsShutDown || e.CloseReason == CloseReason.ApplicationExitCall ||
            e.CloseReason == CloseReason.TaskManagerClosing) return;
        if (!Settings.Animations) { if (BackdropAlive) backdrop.HideNow(); Opacity = 0; return; }   // animations off: gone at once, glass and all
        e.Cancel = true;
        if (closing) return;   // already on its way out
        closing = true;
        busy = true;           // nothing else reacts while it closes
        animTimer.Stop();
        hoverTimer.Stop();
        if (BackdropAlive) backdrop.AnimateOut((int)CloseMs);
        double from = Opacity;
        var clock = Stopwatch.StartNew();
        var t = new System.Windows.Forms.Timer { Interval = 8 };
        t.Tick += (s, a) =>
        {
            double f = Math.Min(1, clock.Elapsed.TotalMilliseconds / CloseMs);
            if (!IsDisposed) Opacity = from * (1 - f * f);   // ease-in: starts gently, finishes fast
            if (f >= 1)
            {
                t.Dispose();
                closeAnimDone = true;
                if (!IsDisposed) Close();
            }
        };
        t.Start();
    }

    void AnimateIn()
    {
        if (!Settings.Animations) { Anim.ShowNow(this); return; }   // animations off: there at once
        if (BackdropAlive) backdrop.AnimateIn();
        animTarget = Location;
        bool rise = !BackdropAlive;
        if (rise) Location = new Point(animTarget.X, animTarget.Y + Px(10));
        animClock.Restart();
        animTimer.Tick += (s, e) =>
        {
            if (IsDisposed) { animTimer.Stop(); return; }
            double ms = animClock.Elapsed.TotalMilliseconds;
            double f = Math.Min(1, ms / FadeMs);
            Opacity = 1 - Math.Pow(1 - f, 3);   // ease-out
            if (rise)
            {
                double r = Math.Min(1, ms / RiseMs);
                // a small overshoot, like Prism's cubic-bezier(.3,1.5,.5,1) "settle"
                double k = 1 + 2.2 * Math.Pow(r - 1, 3) + 1.2 * Math.Pow(r - 1, 2);
                Location = new Point(animTarget.X, animTarget.Y + (int)Math.Round(Px(10) * (1 - k)));
            }
            if (f >= 1 && (!rise || ms >= RiseMs))
            {
                animTimer.Stop();
                Opacity = 1;
                Location = animTarget;
            }
        };
        animTimer.Start();
    }

    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();

    readonly DateTime openedAt = DateTime.Now;
    public bool Interacted;   // the user clicked or typed in it (a hover-opened folder then stays until you click away)

    protected override void OnDeactivate(EventArgs e)
    {
        base.OnDeactivate(e);
        if (busy || !ready) return;
        if (titleBox.Focused) CommitTitle();
        // Decide right after this message: by then Windows has settled which window is in front.
        BeginInvoke((Action)CloseIfClickedAway);
    }

    bool ready;   // fully open (focus changes while the window is still being set up don't count)

    // One of this folder's own menus / cards / boxes (or the glass behind one)?
    bool IsMyPopup(IntPtr h)
    {
        foreach (Form f in Application.OpenForms)
        {
            if (!f.IsHandleCreated || f.Handle != h) continue;
            Form owner = f is GlassBackdrop ? ((GlassBackdrop)f).Host : f;
            var child = owner as IFolderChild;
            return child != null && child.Folder == this;
        }
        return false;
    }

    void CloseIfClickedAway()
    {
        if (IsDisposed || busy || Form.ActiveForm == this) return;
        IntPtr fg = GetForegroundWindow();
        // Clicking a see-through part lands on the blur window behind it; that's still "inside".
        if (fg == Handle || (BackdropAlive && fg == backdrop.Handle) || IsMyPopup(fg)) return;
        // A brand-new folder (New App Folder) can lose focus to Explorer for a moment as it's created.
        if (StartRenaming && !Interacted && (DateTime.Now - openedAt).TotalMilliseconds < 600)
        {
            Activate(); BeginRename();
            return;
        }
        Close();   // clicked on the Desktop or another app: close straight away
    }

    // Hover-opened folders close as soon as the pointer moves away, unless you've started using them.
    void CheckPointerLeft()
    {
        if (!HoverMode || Interacted || busy || IsDisposed) return;
        var zone = Rectangle.Inflate(Bounds, Px(12), Px(12));
        if (zone.Contains(Cursor.Position)) { pointerAwaySince = DateTime.MaxValue; return; }
        if (pointerAwaySince == DateTime.MaxValue) pointerAwaySince = DateTime.Now;
        else if ((DateTime.Now - pointerAwaySince).TotalMilliseconds > 250) Close();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Escape) { if (titleBox.Focused) { titleBox.Text = title; grid.Focus(); } else Close(); }
        else if (e.KeyCode == Keys.V && e.Control && !titleBox.Focused && Clipboard.ContainsFileDropList())
        {
            var list = new List<string>();
            foreach (var f in Clipboard.GetFileDropList()) list.Add(f);
            AddPaths(list, -1);
        }
        base.OnKeyDown(e);
    }

    // Lets you drag the window by its empty top area.
    protected override void WndProc(ref Message m)
    {
        base.WndProc(ref m);
        if (m.Msg == 0x84 && (int)m.Result == 1) // WM_NCHITTEST -> HTCLIENT
        {
            var p = PointToClient(new Point((short)((long)m.LParam & 0xFFFF), (short)(((long)m.LParam >> 16) & 0xFFFF)));
            if (p.Y < Px(14)) m.Result = (IntPtr)2; // HTCAPTION
        }
    }

    // ---------- helpers ----------
    // Text on a coloured shape (e.g. black on a blue pill), centred in r. A patch of exactly that
    // colour goes behind the letters first, so their smooth edges blend into the colour rather than
    // into the see-through window's invisible background (which made them look jagged and heavy).
    // The patch matches the shape behind it, so it can't be seen.
    public static void TextOnColor(Graphics g, string text, Font font, Rectangle r, Color textColor, Color bg)
    {
        var size = TextRenderer.MeasureText(g, text, font, r.Size, TextFormatFlags.SingleLine | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
        float pad = 3 * S, h = Math.Min(r.Height - 4 * S, size.Height + 2 * pad), w = Math.Min(r.Width - r.Height, size.Width + 2 * pad);
        // whole pixels, no smoothing: a soft patch edge would blend into the invisible background too
        var patch = Rectangle.Round(new RectangleF(r.X + (r.Width - w) / 2f, r.Y + (r.Height - h) / 2f, w, h));
        var oldMode = g.SmoothingMode;
        g.SmoothingMode = SmoothingMode.None;
        g.PixelOffsetMode = PixelOffsetMode.None;
        using (var b = new SolidBrush(bg)) g.FillRectangle(b, patch);
        g.SmoothingMode = oldMode;
        // ClearType, blending into the patch's exact colour: as sharp as Windows' own text
        TextRenderer.DrawText(g, text, font, r, textColor, bg, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter |
            TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding);
    }

    // Text, drawn with ClearType (Windows' own sharp text), on both looks. On glass its edges blend
    // into the see-through colour, which is close to the glass behind, so it stays crisp there too.
    public static void DrawLabel(Graphics g, string text, Font font, Rectangle r, Color color, bool center)
    {
        TextRenderer.DrawText(g, text, font, r, color, (center ? TextFormatFlags.HorizontalCenter : TextFormatFlags.Left | TextFormatFlags.SingleLine) |
            TextFormatFlags.WordBreak | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding |
            (center ? 0 : TextFormatFlags.VerticalCenter));
    }

    // centred ClearType text (pills, buttons)
    public static void TextCentered(Graphics g, string text, Font font, Rectangle r, Color color)
    {
        TextRenderer.DrawText(g, text, font, r, color, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter |
            TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding);
    }

    public static GraphicsPath Rounded(RectangleF r, float rad)
    {
        var p = new GraphicsPath();
        float d = rad * 2;
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    string Prompt(string caption, string label, string value)
    {
        // Prysm's own text box card, centred over the folder
        using (var f = new PromptCard(this, caption, label, value))
        {
            f.Location = new Point(Left + (Width - f.Width) / 2, Top + Px(60));
            busy = true;
            f.ShowDialog(this);
            busy = false;
            if (!IsDisposed) Activate();
            return f.Result;
        }
    }
}

// Menu colours from the Prism theme (named DarkColors for history; follows light mode too).
class DarkColors : ProfessionalColorTable
{
    static Color Bg { get { return Theme.Panel; } }
    static Color Hi { get { return Theme.Ink08; } }
    public override Color ToolStripDropDownBackground { get { return Bg; } }
    public override Color ImageMarginGradientBegin { get { return Bg; } }
    public override Color ImageMarginGradientMiddle { get { return Bg; } }
    public override Color ImageMarginGradientEnd { get { return Bg; } }
    public override Color MenuItemSelected { get { return Hi; } }
    public override Color MenuItemSelectedGradientBegin { get { return Hi; } }
    public override Color MenuItemSelectedGradientEnd { get { return Hi; } }
    public override Color MenuItemBorder { get { return Hi; } }
    public override Color MenuBorder { get { return Theme.Rim; } }
    public override Color SeparatorDark { get { return Theme.Line; } }
    public override Color SeparatorLight { get { return Bg; } }
    public override Color CheckBackground { get { return Theme.Ink12; } }
    public override Color CheckSelectedBackground { get { return Theme.Ink12; } }
    public override Color CheckPressedBackground { get { return Theme.Ink12; } }
}

// Mark a folder as "in use" on any click or key press inside it (or on its glass backdrop).
class InteractionFilter : IMessageFilter
{
    public bool PreFilterMessage(ref Message m)
    {
        if (m.Msg != 0x201 && m.Msg != 0x204 && m.Msg != 0x100) return false;   // L/R button down, key down
        var c = Control.FromChildHandle(m.HWnd);
        Form f = c == null ? null : c.FindForm();
        var gb = f as GlassBackdrop;
        if (gb != null) f = gb.Host;
        if (f is IFolderChild) f = ((IFolderChild)f).Folder;
        var ff = f as FolderForm;
        if (ff == null && f != null) ff = f.Owner as FolderForm;   // the rename prompt
        if (ff != null) ff.Interacted = true;
        return false;
    }
}

// The gear's settings, as a small Prism card (same glass, corners, type and accent as the
// folder): a Windows 11 toggle for open-on-hover and a two-way switch for the look.
class SettingsCard : Form, IGlassHost, IFolderChild, IDecoHost
{
    readonly FolderForm folder;
    readonly Action<bool> setLook;
    GlassBackdrop glass;
    Rectangle animRow, animToggle;
    const int RowsShift = 50;   // everything below the Animations row moves down by this much
    Rectangle hoverRow, toggleRect, segGlass, segSolid, seg2, seg3, segRound, segSharp, updBtn;
    int hot = -1;   // 0 hover row, 1 See-through, 2 Solid, 3 icon 2x2, 4 icon 3x3, 5 Rounded, 6 Sharper, 7 update button

    // the Updates row: what it says and what its button does
    enum Upd { Idle, Checking, UpToDate, Available, Downloading, Installing, Failed }
    Upd upd = Updater.Available ? Upd.Available : Upd.Idle;
    int updPct;
    string updError = "";
    readonly Font titleFont = Theme.UiFont(11f, FontStyle.Regular, true);
    readonly Font font = Theme.UiFont(9.5f);
    readonly Font small = Theme.UiFont(8.5f);
    static int Px(float v) { return (int)Math.Round(v * FolderForm.S); }

    public SettingsCard(FolderForm folder, Action<bool> setLook)
    {
        this.folder = folder;
        this.setLook = setLook;
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        ShowInTaskbar = false;
        TopMost = true;
        KeyPreview = true;
        BackColor = Theme.Bg;
        TransparencyKey = Theme.Key;
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint, true);
        ClientSize = new Size(Px(292), Px(410 + RowsShift));
        int pad = Px(14);
        hoverRow = new Rectangle(pad - Px(6), Px(46), ClientSize.Width - 2 * pad + Px(12), Px(48));
        toggleRect = new Rectangle(ClientSize.Width - pad - Px(40), hoverRow.Y + (hoverRow.Height - Px(20)) / 2, Px(40), Px(20));
        animRow = new Rectangle(hoverRow.X, hoverRow.Bottom + Px(2), hoverRow.Width, hoverRow.Height);
        animToggle = new Rectangle(toggleRect.X, animRow.Y + (animRow.Height - Px(20)) / 2, Px(40), Px(20));
        int D = RowsShift;
        int segY = Px(130 + D), segH = Px(36), half = (ClientSize.Width - 2 * pad) / 2;
        segGlass = new Rectangle(pad, segY, half, segH);
        segSolid = new Rectangle(pad + half, segY, ClientSize.Width - 2 * pad - half, segH);
        int gridY = Px(210 + D);
        seg2 = new Rectangle(pad, gridY, half, segH);
        seg3 = new Rectangle(pad + half, gridY, ClientSize.Width - 2 * pad - half, segH);
        int cornerY = Px(290 + D);
        segRound = new Rectangle(pad, cornerY, half, segH);
        segSharp = new Rectangle(pad + half, cornerY, ClientSize.Width - 2 * pad - half, segH);
        int bw = Px(104);
        updBtn = new Rectangle(ClientSize.Width - pad - bw, Px(358 + D), bw, Px(32));
        Cursor = Cursors.Hand;
    }

    protected override void OnLoad(EventArgs e)
    {
        Opacity = 0;
        base.OnLoad(e);
        // the card layer behind: glass on See-through, the panel on Solid (smooth corners either way)
        {
            glass = new GlassBackdrop(this, 16);
            glass.Show();
            var owner = Owner;
            glass.Owner = owner;      // keep the stacking: folder < glass < card
            Owner = glass;
        }
        int round = glass != null && glass.Rounded ? 1 : 2, rim = glass != null && glass.Rounded ? unchecked((int)0xFFFFFFFE) : Theme.ToBgr(Theme.Rim);
        int dark = Theme.Light ? 0 : 1, off = 1;
        try
        {
            Native.DwmSetWindowAttribute(Handle, 33, ref round, 4);
            Native.DwmSetWindowAttribute(Handle, 34, ref rim, 4);
            Native.DwmSetWindowAttribute(Handle, 20, ref dark, 4);
            Native.DwmSetWindowAttribute(Handle, 3, ref off, 4);
        }
        catch { }
    }

    protected override CreateParams CreateParams
    {
        get { var cp = base.CreateParams; cp.ExStyle |= 0x80; return cp; }
    }

    // pops in like the folder: glass springs, content fades
    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        if (!Settings.Animations) { Anim.ShowNow(this); return; }
        if (glass != null && !glass.IsDisposed) glass.AnimateIn();
        var clock = Stopwatch.StartNew();
        var t = new System.Windows.Forms.Timer { Interval = 8 };
        t.Tick += (s, a) =>
        {
            if (IsDisposed) { t.Dispose(); return; }
            double f = Math.Min(1, clock.Elapsed.TotalMilliseconds / 140);
            Opacity = 1 - Math.Pow(1 - f, 3);
            if (f >= 1) { t.Dispose(); Opacity = 1; }
        };
        t.Start();
    }

    protected override void OnLocationChanged(EventArgs e) { base.OnLocationChanged(e); if (glass != null && !glass.IsDisposed) glass.Bounds = Bounds; }
    protected override void OnDeactivate(EventArgs e) { base.OnDeactivate(e); BeginInvoke((Action)(() => { if (!IsDisposed && Form.ActiveForm != this) Close(); })); }
    protected override void OnKeyDown(KeyEventArgs e) { if (e.KeyCode == Keys.Escape) Close(); base.OnKeyDown(e); }
    // closes with the same quick shrink-and-fade as the folder
    bool closeAnimDone, closing;
    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        base.OnFormClosing(e);
        if (e.Cancel || closeAnimDone || !Visible || e.CloseReason == CloseReason.WindowsShutDown || e.CloseReason == CloseReason.ApplicationExitCall) return;
        if (!Settings.Animations) { if (glass != null && !glass.IsDisposed) glass.HideNow(); Opacity = 0; return; }
        e.Cancel = true;
        if (closing) return;
        closing = true;
        if (glass != null && !glass.IsDisposed) glass.AnimateOut(100);
        double from = Opacity;
        var clock = Stopwatch.StartNew();
        var t = new System.Windows.Forms.Timer { Interval = 8 };
        t.Tick += (s, a) =>
        {
            double f = Math.Min(1, clock.Elapsed.TotalMilliseconds / 100);
            if (!IsDisposed) Opacity = from * (1 - f * f);
            if (f >= 1) { t.Dispose(); closeAnimDone = true; if (!IsDisposed) Close(); }
        };
        t.Start();
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        base.OnFormClosed(e);
        if (glass != null && !glass.IsDisposed) { try { Owner = null; glass.Close(); glass.Dispose(); } catch { } }
    }

    int HitTest(Point p)
    {
        return hoverRow.Contains(p) ? 0 : segGlass.Contains(p) ? 1 : segSolid.Contains(p) ? 2
             : seg2.Contains(p) ? 3 : seg3.Contains(p) ? 4
             : segRound.Contains(p) ? 5 : segSharp.Contains(p) ? 6 : updBtn.Contains(p) ? 7 : animRow.Contains(p) ? 8 : -1;
    }

    protected override void OnMouseMove(MouseEventArgs e) { base.OnMouseMove(e); SetHot(HitTest(e.Location)); }
    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); SetHot(-1); }
    protected override void OnMouseUp(MouseEventArgs e) { base.OnMouseUp(e); HandleClick(e.Location); }
    void SetHot(int h) { if (h != hot) { hot = h; Invalidate(); } }
    public FolderForm Folder { get { return folder; } }
    public int PromoHot { set { hot = value; Refresh(); } }   // for the demo GIF
    public Rectangle PromoSolidRect { get { return segSolid; } }

    void HandleClick(Point p)
    {
        switch (HitTest(p))
        {
            case 0:
                if (!Installer.IsInstalled) return;
                Settings.HoverOpen = !Settings.HoverOpen;
                Invalidate();
                break;
            case 1: if (!Theme.Glass) { Close(); setLook(true); } break;
            case 2: if (Theme.Glass) { Close(); setLook(false); } break;
            case 3: case 4:
                bool grid3 = HitTest(p) == 4;
                if (grid3 == Theme.Grid3) return;
                Settings.IconGrid3 = grid3;
                Theme.Grid3 = grid3;
                Invalidate();
                Update();
                FolderForm.RefreshAllIcons();   // redraw every folder's Desktop icon with the new grid
                break;
            case 5: if (Theme.Sharp) { Close(); folder.SetCorners(false); } break;
            case 6: if (!Theme.Sharp) { Close(); folder.SetCorners(true); } break;
            case 7: UpdateButton(); break;
            case 8:
                Settings.Animations = !Settings.Animations;
                Invalidate();
                break;
        }
    }

    // clicks on the see-through parts land on the glass behind
    public void GlassMouseMove(Point p) { SetHot(HitTest(p)); }
    public void GlassMouseDown(MouseEventArgs e) { }
    public void GlassMouseUp(MouseEventArgs e) { if (e.Button == MouseButtons.Left) HandleClick(PointToClient(Cursor.Position)); }
    public void GlassDragOver(DragEventArgs e) { e.Effect = DragDropEffects.None; }
    public void GlassDrop(DragEventArgs e) { }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Theme.Bg);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        int pad = Px(14);
        FolderForm.DrawLabel(g, "Settings", titleFont, new Rectangle(pad, Px(10), Width - 2 * pad, Px(28)), Theme.Fg, false);

        // the shapes (row highlight, toggle, pill tracks, selected pills) are drawn smoothly in the
        // glass layer; without it they're painted here
        if (DecosOn) RequestDecos();
        else { var l = new List<Deco>(); CollectDecos(l); Decor.Paint(g, l); }

        // Row: Open by hovering  [toggle]
        FolderForm.DrawLabel(g, "Open by hovering", font, new Rectangle(pad, hoverRow.Y + Px(5), toggleRect.Left - pad - Px(8), Px(20)), Theme.Fg, false);
        FolderForm.DrawLabel(g, "Point at a folder to open it", small, new Rectangle(pad, hoverRow.Y + Px(24), toggleRect.Left - pad - Px(8), Px(18)), Theme.Dim, false);

        // Row: Animations  [toggle]
        FolderForm.DrawLabel(g, "Animations", font, new Rectangle(pad, animRow.Y + Px(5), animToggle.Left - pad - Px(8), Px(20)), Theme.Fg, false);
        FolderForm.DrawLabel(g, "Off: folders open and close instantly", small, new Rectangle(pad, animRow.Y + Px(24), animToggle.Left - pad - Px(8), Px(18)), Theme.Dim, false);

        int D = RowsShift;
        // Look:  [ See-through | Solid ]
        FolderForm.DrawLabel(g, "Look", small, new Rectangle(pad, Px(104 + D), Width - 2 * pad, Px(20)), Theme.Dim, false);
        SegmentText(g, segGlass, "See-through", Theme.Glass);
        SegmentText(g, segSolid, "Solid", !Theme.Glass);

        // Desktop icon:  [ 2 × 2 | 3 × 3 ]   (3 × 3 kicks in once a folder has more than 4 apps)
        FolderForm.DrawLabel(g, "Desktop icon, with more than 4 apps", small, new Rectangle(pad, Px(184 + D), Width - 2 * pad, Px(20)), Theme.Dim, false);
        SegmentText(g, seg2, "2 × 2", !Theme.Grid3);
        SegmentText(g, seg3, "3 × 3", Theme.Grid3);

        // Corners:  [ Rounded | Sharper ]
        FolderForm.DrawLabel(g, "Corners", small, new Rectangle(pad, Px(264 + D), Width - 2 * pad, Px(20)), Theme.Dim, false);
        SegmentText(g, segRound, "Rounded", !Theme.Sharp);
        SegmentText(g, segSharp, "Sharper", Theme.Sharp);

        // Updates:  Version 1.3.0 / status        [ Check | Update ]
        FolderForm.DrawLabel(g, "Updates", small, new Rectangle(pad, Px(336 + D), Width - 2 * pad, Px(20)), Theme.Dim, false);
        FolderForm.DrawLabel(g, "Version " + Updater.CurrentText, font, new Rectangle(pad, updBtn.Y - Px(2), updBtn.Left - pad - Px(8), Px(20)), Theme.Fg, false);
        FolderForm.DrawLabel(g, UpdateStatus(), small, new Rectangle(pad, updBtn.Y + Px(17), updBtn.Left - pad - Px(4), Px(18)),
                             upd == Upd.Available ? Theme.Accent : upd == Upd.Failed ? Color.FromArgb(0xff, 0x8a, 0x8a) : Theme.Dim, false);
        if (upd == Upd.Available) FolderForm.TextOnColor(g, UpdateButtonText(), font, updBtn, Color.Black, hot == 7 ? ControlPaint.Light(Theme.Accent, .25f) : Theme.Accent);
        else TextAt(g, UpdateButtonText(), font, updBtn, Theme.Fg);
    }

    // ---- updates ----
    void UpdateButton()
    {
        if (upd == Upd.Checking || upd == Upd.Downloading || upd == Upd.Installing) return;
        if (upd == Upd.Available) { StartDownload(); return; }
        upd = Upd.Checking; Invalidate();
        RunInBackground(() =>
        {
            var r = Updater.FetchLatest();
            Updater.Remember(r);
            return Updater.Available ? Upd.Available : Upd.UpToDate;
        });
    }

    void StartDownload()
    {
        upd = Upd.Downloading; updPct = 0; Invalidate();
        string url = Settings.LatestUrl;
        RunInBackground(() =>
        {
            string exe = Updater.Download(url, pct => { try { BeginInvoke((Action)(() => { updPct = pct; Invalidate(); })); } catch { } });
            Updater.Install(exe);   // the new version replaces this one and restarts Prysm Folder
            return Upd.Installing;
        });
    }

    // network work off the UI thread; the row shows the outcome (or the error)
    void RunInBackground(Func<Upd> work)
    {
        new Thread(() =>
        {
            Upd result; string err = "";
            try { result = work(); }
            catch (Exception ex) { result = Upd.Failed; err = ex.Message; HoverWatcher.Log("update: " + ex.Message); }
            try { BeginInvoke((Action)(() => { upd = result; updError = err; Invalidate(); })); } catch { }
        }) { IsBackground = true }.Start();
    }

    string UpdateStatus()
    {
        switch (upd)
        {
            case Upd.Checking: return "Checking for updates…";
            case Upd.UpToDate: return "You're up to date";
            case Upd.Available: return "Version " + Settings.LatestVersion + " is available";
            case Upd.Downloading: return "Downloading… " + updPct + "%";
            case Upd.Installing: return "Installing — Prysm Folder will restart";
            case Upd.Failed: return "Couldn't check right now";
            default: return "Updates come from GitHub";
        }
    }

    string UpdateButtonText()
    {
        switch (upd)
        {
            case Upd.Available: return "Update";
            case Upd.Checking: case Upd.Downloading: case Upd.Installing: return "…";
            case Upd.Failed: return "Try again";
            default: return "Check";
        }
    }

    // ---- shapes (see Deco) ----
    public bool DecosOn { get { return !Decor.Disabled && glass != null && !glass.IsDisposed && glass.Rounded; } }
    public RectangleF DecoClip { get { return RectangleF.Empty; } }
    bool decosPending;
    public void RequestDecos()
    {
        if (decosPending || !IsHandleCreated || IsDisposed) return;
        decosPending = true;
        BeginInvoke((Action)(() => { decosPending = false; if (DecosOn) glass.UpdateDecos(); }));
    }

    public void CollectDecos(List<Deco> list)
    {
        float s = FolderForm.S;
        if (hot == 0) list.Add(Soft(hoverRow, Px(10), Theme.Ink07));
        if (hot == 8) list.Add(Soft(animRow, Px(10), Theme.Ink07));
        Toggle(list, toggleRect, Settings.HoverOpen && Installer.IsInstalled);
        Toggle(list, animToggle, Settings.Animations);
        // the three two-way switches
        Segments(list, segGlass, segSolid, Theme.Glass, 1);
        Segments(list, seg2, seg3, !Theme.Grid3, 3);
        Segments(list, segRound, segSharp, !Theme.Sharp, 5);
        // the update button: Prism blue when there's an update to install
        var u = updBtn;
        if (upd == Upd.Available) list.Add(Deco.Box(u, u.Height / 2f, hot == 7 ? ControlPaint.Light(Theme.Accent, .25f) : Theme.Accent));
        else list.Add(Theme.Glass ? Deco.Outline(u, u.Height / 2f, hot == 7 ? Theme.Dim : Theme.Ink18, 1.2f * s)
                                  : Deco.Box(u, u.Height / 2f, hot == 7 ? Theme.Ink12 : Theme.Ink08));
    }

    // Windows 11 toggle switch, in Prism's accent
    static void Toggle(List<Deco> list, Rectangle t, bool on)
    {
        float s = FolderForm.S;
        list.Add(on ? Deco.Box(t, t.Height / 2f, Theme.Accent) : Deco.Outline(t, t.Height / 2f, Theme.Dim, 1.2f * s));
        float k = on ? t.Height - 8 * s : t.Height - 10 * s;
        float cx = on ? t.Right - t.Height / 2f : t.X + t.Height / 2f, cy = t.Y + t.Height / 2f;
        list.Add(Deco.Circle(new RectangleF(cx - k / 2, cy - k / 2, k, k), on ? Color.Black : Theme.Dim, Color.Empty, 0));
    }

    // the track, the selected pill (Prism blue) and the hovered one
    void Segments(List<Deco> list, Rectangle a, Rectangle b, bool firstSelected, int firstHot)
    {
        var track = Rectangle.Union(a, b);
        list.Add(Theme.Glass ? Deco.Outline(track, track.Height / 2f, Theme.Ink18, 1.2f * FolderForm.S) : Deco.Box(track, track.Height / 2f, Theme.Ink08));
        for (int i = 0; i < 2; i++)
        {
            var inner = Rectangle.Inflate(i == 0 ? a : b, -Px(3), -Px(3));
            bool selected = (i == 0) == firstSelected;
            if (selected) list.Add(Deco.Box(inner, inner.Height / 2f, Theme.Accent));
            else if (hot == firstHot + i) list.Add(Soft(inner, inner.Height / 2f, Theme.Ink12));
        }
    }

    // a soft highlight: filled on Solid, an outline on glass (a fill would look like a sticker)
    static Deco Soft(Rectangle r, float rad, Color fill)
    {
        return Theme.Glass ? Deco.Outline(r, rad, Theme.Ink18, 1.2f * FolderForm.S) : Deco.Box(r, rad, fill);
    }

    void SegmentText(Graphics g, Rectangle r, string text, bool selected)
    {
        var inner = Rectangle.Inflate(r, -Px(3), -Px(3));
        if (selected) FolderForm.TextOnColor(g, text, font, inner, Color.Black, Theme.Accent);
        else TextAt(g, text, font, inner, Theme.Fg2);
    }

    static void TextAt(Graphics g, string text, Font f, Rectangle r, Color c) { FolderForm.TextCentered(g, text, f, r, c); }

}

// Base for Prysm's little popups (menus, the rename box): a borderless Prism card on the same
// glass as the folder (or the solid panel), rounded corners, quick fade/shrink in and out.
abstract class PrismPopup : Form, IGlassHost, IFolderChild, IDecoHost
{
    protected readonly FolderForm folder;
    protected GlassBackdrop glass;
    readonly float radius;
    protected static int Px(float v) { return (int)Math.Round(v * FolderForm.S); }
    public FolderForm Folder { get { return folder; } }

    protected PrismPopup(FolderForm folder, float radius)
    {
        this.folder = folder;
        this.radius = radius;
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        ShowInTaskbar = false;
        TopMost = true;
        KeyPreview = true;
        BackColor = Theme.Bg;
        ForeColor = Theme.Fg;
        Font = Theme.UiFont(9.5f);
        TransparencyKey = Theme.Key;
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint, true);
    }

    protected override CreateParams CreateParams
    {
        get { var cp = base.CreateParams; cp.ExStyle |= 0x80; return cp; }
    }

    // keep it on screen
    public void PlaceAt(Point screen)
    {
        var wa = Screen.FromPoint(screen).WorkingArea;
        int x = Math.Max(wa.Left + 4, Math.Min(screen.X, wa.Right - Width - 4));
        int y = screen.Y + Height > wa.Bottom - 4 ? Math.Max(wa.Top + 4, screen.Y - Height) : screen.Y;
        Location = new Point(x, y);
    }

    protected override void OnLoad(EventArgs e)
    {
        Opacity = 0;
        base.OnLoad(e);
        // the card layer behind: glass on See-through, the panel on Solid (smooth corners either way)
        {
            glass = new GlassBackdrop(this, radius);
            glass.Show();
            glass.Owner = Owner;
            if (!Modal) Owner = glass;   // glass right behind, this right in front (a dialog keeps its owner)
        }
        bool rounded = glass != null && glass.Rounded;
        int round = rounded ? 1 : 2, rim = rounded ? unchecked((int)0xFFFFFFFE) : Theme.ToBgr(Theme.Rim), dark = Theme.Light ? 0 : 1, off = 1;
        try
        {
            Native.DwmSetWindowAttribute(Handle, 33, ref round, 4);
            Native.DwmSetWindowAttribute(Handle, 34, ref rim, 4);
            Native.DwmSetWindowAttribute(Handle, 20, ref dark, 4);
            Native.DwmSetWindowAttribute(Handle, 3, ref off, 4);
        }
        catch { }
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        Activate();
        if (!Settings.Animations) { Anim.ShowNow(this); return; }
        if (glass != null && !glass.IsDisposed) glass.AnimateIn();
        Fade(0, 1, 120, null);
    }

    protected override void OnLocationChanged(EventArgs e) { base.OnLocationChanged(e); if (glass != null && !glass.IsDisposed) glass.Bounds = Bounds; }

    void Fade(double from, double to, double ms, Action done)
    {
        var clock = Stopwatch.StartNew();
        var t = new System.Windows.Forms.Timer { Interval = 8 };
        t.Tick += (s, a) =>
        {
            double f = Math.Min(1, clock.Elapsed.TotalMilliseconds / ms);
            double k = to > from ? 1 - Math.Pow(1 - f, 3) : f * f;
            if (!IsDisposed) Opacity = from + (to - from) * k;
            if (f >= 1) { t.Dispose(); if (done != null) done(); }
        };
        t.Start();
    }

    bool closeAnimDone, closing;
    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        base.OnFormClosing(e);
        if (e.Cancel || closeAnimDone || !Visible || e.CloseReason == CloseReason.WindowsShutDown || e.CloseReason == CloseReason.ApplicationExitCall) return;
        if (!Settings.Animations) { if (glass != null && !glass.IsDisposed) glass.HideNow(); Opacity = 0; return; }
        e.Cancel = true;
        if (closing) return;
        closing = true;
        if (glass != null && !glass.IsDisposed) glass.AnimateOut(90);
        Fade(Opacity, 0, 90, () => { closeAnimDone = true; if (!IsDisposed) Close(); });
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        base.OnFormClosed(e);
        if (glass != null && !glass.IsDisposed) { try { Owner = null; glass.Close(); glass.Dispose(); } catch { } }
    }

    // pointer and clicks over the see-through parts arrive via the glass
    public virtual void GlassMouseMove(Point p) { }
    public virtual void GlassMouseDown(MouseEventArgs e) { }
    public virtual void GlassMouseUp(MouseEventArgs e) { }
    public void GlassDragOver(DragEventArgs e) { e.Effect = DragDropEffects.None; }
    public void GlassDrop(DragEventArgs e) { }

    // a soft highlight: filled on the solid look, an outline on glass (a fill would look like a sticker)
    protected static Deco Highlight(Rectangle r, float rad)
    {
        return Theme.Glass ? Deco.Outline(r, rad, Theme.Ink18, 1.2f * FolderForm.S) : Deco.Box(r, rad, Theme.Ink08);
    }

    // ---- shapes (see Deco): drawn smoothly in the glass layer, or painted here without it ----
    public bool DecosOn { get { return !Decor.Disabled && glass != null && !glass.IsDisposed && glass.Rounded; } }
    public RectangleF DecoClip { get { return RectangleF.Empty; } }
    public virtual void CollectDecos(List<Deco> list) { }
    bool decosPending;
    public void RequestDecos()
    {
        if (decosPending || !IsHandleCreated || IsDisposed) return;
        decosPending = true;
        BeginInvoke((Action)(() => { decosPending = false; if (DecosOn) glass.UpdateDecos(); }));
    }
    protected void PaintDecos(Graphics g)
    {
        if (DecosOn) RequestDecos();
        else { var l = new List<Deco>(); CollectDecos(l); Decor.Paint(g, l); }
    }
}

// Prysm's right-click menus: rows with Windows 11 icons, separators, and a red "Remove".
class PrismMenu : PrismPopup
{
    class Entry { public string Text; public char Glyph; public Action Do; public bool Separator, Danger; public Rectangle Bounds; }
    readonly List<Entry> entries = new List<Entry>();
    int hot = -1;
    public Action Chosen;   // what was picked (run by the folder once the menu has gone)
    static Font iconFont;

    public PrismMenu(FolderForm folder) : base(folder, 12) { Cursor = Cursors.Hand; }

    public void Add(string text, char glyph, Action action, bool danger = false) { entries.Add(new Entry { Text = text, Glyph = glyph, Do = action, Danger = danger }); }
    public int PromoHot { set { hot = value; Refresh(); } }                    // for the demo GIF
    public Rectangle PromoRow(int i) { return entries[i].Bounds; }
    public int PromoCount { get { return entries.Count; } }
    public bool PromoIsSeparator(int i) { return entries[i].Separator; }
    public void AddSeparator() { entries.Add(new Entry { Separator = true }); }

    public void Measure()
    {
        int pad = Px(5), rowH = Px(34), sepH = Px(9), textW = 0;
        foreach (var en in entries)
            if (!en.Separator) textW = Math.Max(textW, TextRenderer.MeasureText(en.Text, Font).Width);
        int w = Math.Max(Px(210), textW + Px(12 + 28 + 18));
        int y = pad;
        foreach (var en in entries)
        {
            int h = en.Separator ? sepH : rowH;
            en.Bounds = new Rectangle(pad, y, w - 2 * pad, h);
            y += h;
        }
        ClientSize = new Size(w, y + pad);
    }

    int HitTest(Point p)
    {
        for (int i = 0; i < entries.Count; i++) if (!entries[i].Separator && entries[i].Bounds.Contains(p)) return i;
        return -1;
    }
    void SetHot(int h) { if (h != hot) { hot = h; Invalidate(); } }
    void Pick(Point p)
    {
        int i = HitTest(p);
        if (i < 0) return;
        Chosen = entries[i].Do;
        Close();
    }

    protected override void OnMouseMove(MouseEventArgs e) { base.OnMouseMove(e); SetHot(HitTest(e.Location)); }
    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); SetHot(-1); }
    protected override void OnMouseUp(MouseEventArgs e) { base.OnMouseUp(e); Pick(e.Location); }
    public override void GlassMouseMove(Point p) { SetHot(HitTest(p)); }
    public override void GlassMouseUp(MouseEventArgs e) { Pick(PointToClient(Cursor.Position)); }
    protected override void OnDeactivate(EventArgs e) { base.OnDeactivate(e); BeginInvoke((Action)(() => { if (!IsDisposed && Form.ActiveForm != this) Close(); })); }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Escape) Close();
        else if (e.KeyCode == Keys.Down || e.KeyCode == Keys.Up)
        {
            int n = entries.Count, i = hot;
            for (int k = 0; k < n; k++)
            {
                i = (i + (e.KeyCode == Keys.Down ? 1 : -1) + n) % n;
                if (!entries[i].Separator) break;
            }
            SetHot(i);
        }
        else if (e.KeyCode == Keys.Enter && hot >= 0) { Chosen = entries[hot].Do; Close(); }
        base.OnKeyDown(e);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Theme.Bg);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        if (iconFont == null) iconFont = Theme.IconFont(10.5f);
        PaintDecos(g);
        for (int i = 0; i < entries.Count; i++)
        {
            var en = entries[i];
            if (en.Separator) continue;
            Color c = en.Danger ? Color.FromArgb(0xff, 0x6b, 0x6b) : (i == hot ? Theme.Fg : Theme.Fg2);
            var gr = new Rectangle(en.Bounds.X + Px(8), en.Bounds.Y, Px(24), en.Bounds.Height);
            FolderForm.TextCentered(g, en.Glyph.ToString(), iconFont, gr, en.Danger ? c : (i == hot ? Theme.Accent : Theme.Dim));
            var tr = new Rectangle(gr.Right + Px(8), en.Bounds.Y, en.Bounds.Right - gr.Right - Px(12), en.Bounds.Height);
            FolderForm.DrawLabel(g, en.Text, Font, tr, c, false);
        }
    }

    // the hovered row's highlight and the dividers
    public override void CollectDecos(List<Deco> list)
    {
        for (int i = 0; i < entries.Count; i++)
        {
            var en = entries[i];
            if (en.Separator)
            {
                float y = en.Bounds.Y + en.Bounds.Height / 2f;
                list.Add(Deco.Ln(en.Bounds.X + Px(10), y, en.Bounds.Right - Px(10), y, Theme.Ink18, 1f));
            }
            else if (i == hot) list.Add(Highlight(en.Bounds, Px(Theme.Radius(8))));
        }
    }
}

// Prysm's text box popup (Rename, Add website, New folder): a Prism card with a field and two pill buttons.
class PromptCard : PrismPopup
{
    readonly TextBox box = new TextBox();
    readonly string caption, label;
    Rectangle field, okR, cancelR;
    int hot = -1;   // 0 OK, 1 Cancel
    readonly Font titleFont = Theme.UiFont(11f, FontStyle.Regular, true);
    readonly Font small = Theme.UiFont(8.5f);
    public string Result;

    public PromptCard(FolderForm folder, string caption, string label, string value) : base(folder, 16)
    {
        this.caption = caption; this.label = label;
        ClientSize = new Size(Px(340), Px(170));
        int pad = Px(16);
        field = new Rectangle(pad, Px(66), ClientSize.Width - 2 * pad, Px(34));
        box.BorderStyle = BorderStyle.None;
        box.BackColor = Theme.Ink08;
        box.ForeColor = Theme.Fg;
        box.Font = Theme.UiFont(10f);
        box.Text = value;
        box.SetBounds(field.X + Px(10), field.Y + (field.Height - box.PreferredHeight) / 2, field.Width - Px(20), box.PreferredHeight);
        Controls.Add(box);
        int bw = Px(92), bh = Px(32), by = ClientSize.Height - pad - bh;
        okR = new Rectangle(ClientSize.Width - pad - bw, by, bw, bh);
        cancelR = new Rectangle(okR.X - Px(8) - bw, by, bw, bh);
        box.GotFocus += (s, e) => Invalidate();
        box.LostFocus += (s, e) => Invalidate();
        box.KeyDown += (s, e) =>
        {
            if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; Finish(true); }
            else if (e.KeyCode == Keys.Escape) { e.SuppressKeyPress = true; Finish(false); }
        };
    }

    protected override void OnShown(EventArgs e) { base.OnShown(e); box.Focus(); box.SelectAll(); }

    // (Result, not DialogResult: the close animation briefly cancels the close, which resets DialogResult)
    void Finish(bool ok) { Result = ok ? box.Text.Trim() : null; Close(); }

    int HitTest(Point p) { return okR.Contains(p) ? 0 : cancelR.Contains(p) ? 1 : -1; }
    void SetHot(int h) { if (h != hot) { hot = h; Invalidate(); } }
    void Press(Point p) { int i = HitTest(p); if (i == 0) Finish(true); else if (i == 1) Finish(false); }
    protected override void OnMouseMove(MouseEventArgs e) { base.OnMouseMove(e); SetHot(HitTest(e.Location)); Cursor = hot >= 0 ? Cursors.Hand : Cursors.Default; }
    protected override void OnMouseUp(MouseEventArgs e) { base.OnMouseUp(e); Press(e.Location); }
    public override void GlassMouseMove(Point p) { SetHot(HitTest(p)); }
    public override void GlassMouseUp(MouseEventArgs e) { Press(PointToClient(Cursor.Position)); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Theme.Bg);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        int pad = Px(16);
        FolderForm.DrawLabel(g, caption, titleFont, new Rectangle(pad, Px(12), Width - 2 * pad, Px(28)), Theme.Fg, false);
        FolderForm.DrawLabel(g, label, small, new Rectangle(pad, Px(42), Width - 2 * pad, Px(20)), Theme.Dim, false);
        PaintDecos(g);
        Pill(g, cancelR, "Cancel", false, hot == 1);
        Pill(g, okR, "OK", true, hot == 0);
    }

    // the text field and the two pill buttons
    public override void CollectDecos(List<Deco> list)
    {
        float s = FolderForm.S;
        var f = Deco.Box(field, Px(9), Theme.Ink08);
        f.Stroke = box.Focused ? Theme.Accent : Theme.Ink18;
        f.StrokeW = 1.2f * s;
        list.Add(f);
        list.Add(Deco.Box(okR, okR.Height / 2f, hot == 0 ? ControlPaint.Light(Theme.Accent, .25f) : Theme.Accent));
        list.Add(Theme.Glass ? Deco.Outline(cancelR, cancelR.Height / 2f, hot == 1 ? Theme.Dim : Theme.Ink18, 1.2f * s)
                             : Deco.Box(cancelR, cancelR.Height / 2f, hot == 1 ? Theme.Ink12 : Theme.Ink08));
    }

    void Pill(Graphics g, Rectangle r, string text, bool primary, bool hover)
    {
        if (primary)   // black on the blue OK pill: blended into the blue, not the see-through background
        {
            FolderForm.TextOnColor(g, text, Font, r, Color.Black, hover ? ControlPaint.Light(Theme.Accent, .25f) : Theme.Accent);
            return;
        }
        FolderForm.TextCentered(g, text, Font, r, Theme.Fg);
    }
}

// Windows 11's own folder picker (the same window as File Explorer), allowing several folders.
// WinForms' FolderBrowserDialog is the old tree box, and OpenFileDialog can't choose folders.
static class FolderPicker
{
    [ComImport, Guid("DC1C5A9C-E88A-4dde-A5A1-60F82A20AEF7")] class FileOpenDialogClass { }

    [ComImport, Guid("d57c7288-d4ad-4768-be02-9d969532d960"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IFileOpenDialog
    {
        [PreserveSig] int Show(IntPtr parent);
        void SetFileTypes(uint count, IntPtr specs);
        void SetFileTypeIndex(uint index);
        void GetFileTypeIndex(out uint index);
        void Advise(IntPtr sink, out uint cookie);
        void Unadvise(uint cookie);
        void SetOptions(uint options);
        void GetOptions(out uint options);
        void SetDefaultFolder(IShellItem item);
        void SetFolder(IShellItem item);
        void GetFolder(out IShellItem item);
        void GetCurrentSelection(out IShellItem item);
        void SetFileName([MarshalAs(UnmanagedType.LPWStr)] string name);
        void GetFileName([MarshalAs(UnmanagedType.LPWStr)] out string name);
        void SetTitle([MarshalAs(UnmanagedType.LPWStr)] string title);
        void SetOkButtonLabel([MarshalAs(UnmanagedType.LPWStr)] string label);
        void SetFileNameLabel([MarshalAs(UnmanagedType.LPWStr)] string label);
        void GetResult(out IShellItem item);
        void AddPlace(IShellItem item, int place);
        void SetDefaultExtension([MarshalAs(UnmanagedType.LPWStr)] string ext);
        void Close(int hr);
        void SetClientGuid(ref Guid guid);
        void ClearClientData();
        void SetFilter(IntPtr filter);
        void GetResults(out IShellItemArray items);
        void GetSelectedItems(out IShellItemArray items);
    }

    [ComImport, Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IShellItem
    {
        void BindToHandler(IntPtr pbc, ref Guid bhid, ref Guid riid, out IntPtr ppv);
        void GetParent(out IShellItem parent);
        void GetDisplayName(uint sigdn, [MarshalAs(UnmanagedType.LPWStr)] out string name);
        void GetAttributes(uint mask, out uint attrs);
        void Compare(IShellItem other, uint hint, out int order);
    }

    [ComImport, Guid("b63ea76d-1f85-456f-a19c-48159efa858b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IShellItemArray
    {
        void BindToHandler(IntPtr pbc, ref Guid bhid, ref Guid riid, out IntPtr ppv);
        void GetPropertyStore(int flags, ref Guid riid, out IntPtr ppv);
        void GetPropertyDescriptionList(IntPtr keyType, ref Guid riid, out IntPtr ppv);
        void GetAttributes(int attribFlags, uint mask, out uint attrs);
        void GetCount(out uint count);
        void GetItemAt(uint index, out IShellItem item);
        void EnumItems(out IntPtr ppenum);
    }

    public static List<string> Pick(IntPtr owner, string title)
    {
        var result = new List<string>();
        try
        {
            var d = (IFileOpenDialog)new FileOpenDialogClass();
            d.SetOptions(0x20 | 0x40 | 0x200 | 0x800);   // PICKFOLDERS | FORCEFILESYSTEM | ALLOWMULTISELECT | PATHMUSTEXIST
            d.SetTitle(title);
            d.SetOkButtonLabel("Add");
            if (d.Show(owner) != 0) return result;       // cancelled
            IShellItemArray arr;
            d.GetResults(out arr);
            uint n;
            arr.GetCount(out n);
            for (uint i = 0; i < n; i++)
            {
                IShellItem it;
                arr.GetItemAt(i, out it);
                string path;
                it.GetDisplayName(0x80058000 /*SIGDN_FILESYSPATH*/, out path);
                if (!string.IsNullOrEmpty(path)) result.Add(path);
            }
        }
        catch { }
        return result;
    }
}

// A window that has a GlassBackdrop behind it: clicks and drops on its see-through parts land on
// the glass, which hands them back.
interface IGlassHost
{
    void GlassMouseDown(MouseEventArgs e);
    void GlassMouseUp(MouseEventArgs e);
    void GlassMouseMove(Point client);   // in the host's client coordinates; far outside = left
    void GlassDragOver(DragEventArgs e);
    void GlassDrop(DragEventArgs e);
}

// A popup that belongs to a folder (its menus, settings card, rename box): clicking in one
// doesn't count as clicking away from the folder.
interface IFolderChild { FolderForm Folder { get; } }

[ComImport, Guid("29E691FA-4567-4DCA-B319-D0F207EB6807"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface ICompositorDesktopInterop
{
    void CreateDesktopWindowTarget(IntPtr hwnd, [MarshalAs(UnmanagedType.Bool)] bool isTopmost,
        [MarshalAs(UnmanagedType.IInspectable)] out object target);
}

// The See-through look, made the way Prism makes its Liquid Glass: a window right behind the
// folder that shows Windows' own blurred backdrop (the "host backdrop" acrylic is made of),
// clipped to big 24px corners, with Prism's layers on top: the panel tint, a soft white sheen
// and a hairline rim. The folder's own background is transparent, so the icons and text sit
// crisp on the glass. Falls back to plain acrylic where Windows can't do this.
class GlassBackdrop : Form
{
    public readonly Form Host;
    readonly IGlassHost glassHost;
    readonly float radius;
    public bool Rounded;   // true: glass with big rounded corners (the host should drop its own corner rounding)

    // Windows.UI.Composition needs a dispatcher queue on this thread, made once.
    [DllImport("CoreMessaging.dll")] static extern int CreateDispatcherQueueController(DispatcherQueueOptions o, out IntPtr controller);
    [StructLayout(LayoutKind.Sequential)] struct DispatcherQueueOptions { public int Size, ThreadType, ApartmentType; }
    [ThreadStatic] static IntPtr queue;
    [ThreadStatic] static Windows.UI.Composition.Compositor compositor;
    [ThreadStatic] static bool compositorFailed;

    Windows.UI.Composition.Desktop.DesktopWindowTarget target;
    Windows.UI.Composition.ContainerVisual root;
    readonly List<Windows.UI.Composition.CompositionRoundedRectangleGeometry> geoms = new List<Windows.UI.Composition.CompositionRoundedRectangleGeometry>();
    readonly List<Windows.UI.Composition.Visual> sized = new List<Windows.UI.Composition.Visual>();

    [StructLayout(LayoutKind.Sequential)]
    struct AccentPolicy { public int State, Flags, Gradient, Animation; }
    [StructLayout(LayoutKind.Sequential)]
    struct CompositionData { public int Attribute; public IntPtr Data; public int Size; }
    [DllImport("user32.dll")] static extern int SetWindowCompositionAttribute(IntPtr h, ref CompositionData d);

    public GlassBackdrop(Form host, float radius)
    {
        Host = host;
        glassHost = host as IGlassHost;
        this.radius = Theme.Radius(radius);   // Rounded or Sharper (settings)
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        ShowInTaskbar = false;
        TopMost = true;
        BackColor = Color.Black;
        Bounds = host.Bounds;
        AllowDrop = true;
        Text = host.Text;
        Rounded = UseComposition();
        Activated += (s, e) => { if (!Host.IsDisposed) Host.Activate(); };
        MouseDown += (s, e) => { if (!Host.IsDisposed && glassHost != null) glassHost.GlassMouseDown(e); };
        MouseUp += (s, e) => { if (!Host.IsDisposed && glassHost != null) glassHost.GlassMouseUp(e); };
        MouseMove += (s, e) => { if (!Host.IsDisposed && glassHost != null) glassHost.GlassMouseMove(Host.PointToClient(Cursor.Position)); };
        MouseLeave += (s, e) => { if (!Host.IsDisposed && glassHost != null) glassHost.GlassMouseMove(new Point(-99999, -99999)); };
        DragEnter += (s, e) => { if (!Host.IsDisposed && glassHost != null) glassHost.GlassDragOver(e); };
        DragOver += (s, e) => { if (!Host.IsDisposed && glassHost != null) glassHost.GlassDragOver(e); };
        DragDrop += (s, e) => { if (!Host.IsDisposed && glassHost != null) glassHost.GlassDrop(e); };
    }

    // Gets Windows' compositor ready ahead of time, so the first folder opens just as fast.
    public static void Warm() { UseComposition(); }

    // The host's control shapes (pills, rings, highlights...), drawn smoothly on top of the card.
    Windows.UI.Composition.ShapeVisual decoVisual, decoClipped;

    public void UpdateDecos()
    {
        var host = Host as IDecoHost;
        if (root == null || host == null || IsDisposed) return;
        try
        {
            var c = compositor;
            if (decoVisual == null)
            {
                decoClipped = c.CreateShapeVisual();
                decoVisual = c.CreateShapeVisual();
                root.Children.InsertAtTop(decoClipped);
                root.Children.InsertAtTop(decoVisual);
                sized.Add(decoClipped);
                sized.Add(decoVisual);
                decoClipped.Size = decoVisual.Size = root.Size;
            }
            var list = new List<Deco>();
            host.CollectDecos(list);
            decoVisual.Shapes.Clear();
            decoClipped.Shapes.Clear();
            var clip = host.DecoClip;
            if (!clip.IsEmpty)
                decoClipped.Clip = c.CreateInsetClip(clip.Left, clip.Top, Math.Max(0, Width - clip.Right), Math.Max(0, Height - clip.Bottom));
            foreach (var d in list)
            {
                var shape = MakeShape(c, d);
                if (shape != null) (d.ClipToGrid ? decoClipped : decoVisual).Shapes.Add(shape);
            }
        }
        catch (Exception ex) { HoverWatcher.Log("control shapes: " + ex.Message); }
    }

    static System.Numerics.Vector2 V(float x, float y) { return new System.Numerics.Vector2(x, y); }

    Windows.UI.Composition.CompositionSpriteShape MakeShape(Windows.UI.Composition.Compositor c, Deco d)
    {
        Windows.UI.Composition.CompositionGeometry geo;
        if (d.Line)
        {
            var lg = c.CreateLineGeometry();
            lg.Start = V(d.A.X, d.A.Y);
            lg.End = V(d.B.X, d.B.Y);
            geo = lg;
        }
        else if (d.Ellipse)
        {
            var eg = c.CreateEllipseGeometry();
            eg.Center = V(d.R.X + d.R.Width / 2, d.R.Y + d.R.Height / 2);
            eg.Radius = V(d.R.Width / 2, d.R.Height / 2);
            geo = eg;
        }
        else
        {
            var rg = c.CreateRoundedRectangleGeometry();
            rg.Offset = V(d.R.X, d.R.Y);
            rg.Size = V(d.R.Width, d.R.Height);
            float r = Math.Min(d.Radius, Math.Min(d.R.Width, d.R.Height) / 2);
            rg.CornerRadius = V(r, r);
            geo = rg;
        }
        var s = c.CreateSpriteShape(geo);
        if (!d.Fill.IsEmpty) s.FillBrush = c.CreateColorBrush(C(d.Fill, d.Fill.A / 255.0));
        if (!d.Stroke.IsEmpty)
        {
            s.StrokeBrush = c.CreateColorBrush(C(d.Stroke, d.Stroke.A / 255.0));
            s.StrokeThickness = d.StrokeW;
            if (d.Line) { s.StrokeStartCap = Windows.UI.Composition.CompositionStrokeCap.Round; s.StrokeEndCap = Windows.UI.Composition.CompositionStrokeCap.Round; }
            if (d.Dashed) { s.StrokeDashArray.Add(3f); s.StrokeDashArray.Add(2.5f); }
        }
        return s;
    }

    // Prism's "settle": the glass grows out from slightly smaller with a small spring, fading in.
    // Runs in Windows' compositor, so it's smooth even while the app is busy.
    public void AnimateIn()
    {
        if (root == null) return;
        if (!Settings.Animations) { root.Scale = new System.Numerics.Vector3(1f, 1f, 1f); root.Opacity = 1f; return; }
        try
        {
            var c = compositor;
            root.CenterPoint = new System.Numerics.Vector3(Width / 2f, Height / 2f, 0);
            root.Scale = new System.Numerics.Vector3(0.9f, 0.9f, 1f);
            root.Opacity = 0f;
            var spring = c.CreateSpringVector3Animation();
            spring.FinalValue = new System.Numerics.Vector3(1f, 1f, 1f);
            spring.DampingRatio = 0.62f;
            spring.Period = TimeSpan.FromMilliseconds(38);
            root.StartAnimation("Scale", spring);
            var fade = c.CreateScalarKeyFrameAnimation();
            fade.InsertKeyFrame(1f, 1f, c.CreateCubicBezierEasingFunction(new System.Numerics.Vector2(.2f, 0f), new System.Numerics.Vector2(0f, 1f)));
            fade.Duration = TimeSpan.FromMilliseconds(140);
            root.StartAnimation("Opacity", fade);
        }
        catch { if (root != null) { root.Scale = new System.Numerics.Vector3(1f, 1f, 1f); root.Opacity = 1f; } }
    }

    // Closing: the glass shrinks back a little and fades away (the reverse of AnimateIn, quicker).
    // animations off: vanish in the same frame as the window on top
    public void HideNow()
    {
        try { if (root != null) root.Opacity = 0f; } catch { }
    }

    public void AnimateOut(int ms)
    {
        if (root == null) return;
        try
        {
            var c = compositor;
            root.CenterPoint = new System.Numerics.Vector3(Width / 2f, Height / 2f, 0);
            var ease = c.CreateCubicBezierEasingFunction(new System.Numerics.Vector2(.4f, 0f), new System.Numerics.Vector2(1f, 1f));
            var scale = c.CreateVector3KeyFrameAnimation();
            scale.InsertKeyFrame(1f, new System.Numerics.Vector3(0.93f, 0.93f, 1f), ease);
            scale.Duration = TimeSpan.FromMilliseconds(ms);
            root.StartAnimation("Scale", scale);
            var fade = c.CreateScalarKeyFrameAnimation();
            fade.InsertKeyFrame(1f, 0f, ease);
            fade.Duration = TimeSpan.FromMilliseconds(ms);
            root.StartAnimation("Opacity", fade);
        }
        catch { }
    }

    static bool UseComposition()
    {
        if (compositor != null) return true;
        if (compositorFailed) return false;
        try
        {
            if (queue == IntPtr.Zero)
            {
                var o = new DispatcherQueueOptions { Size = Marshal.SizeOf(typeof(DispatcherQueueOptions)), ThreadType = 2 /*current*/, ApartmentType = 0 };
                if (CreateDispatcherQueueController(o, out queue) != 0) { compositorFailed = true; return false; }
            }
            compositor = new Windows.UI.Composition.Compositor();
            return true;
        }
        catch { compositorFailed = true; return false; }
    }

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= 0x08000000 | 0x80;   // WS_EX_NOACTIVATE (never takes focus) | WS_EX_TOOLWINDOW
            if (Rounded) cp.ExStyle |= 0x00200000;   // WS_EX_NOREDIRECTIONBITMAP: only the composition layers show
            else cp.ClassStyle |= 0x20000;           // drop shadow for the plain-acrylic fallback
            return cp;
        }
    }

    protected override bool ShowWithoutActivation { get { return true; } }

    static Windows.UI.Color C(Color c, double a) { return Windows.UI.Color.FromArgb((byte)Math.Round(255 * a), c.R, c.G, c.B); }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        int none = unchecked((int)0xFFFFFFFE), off = 1, dark = Theme.Light ? 0 : 1;
        try
        {
            Native.DwmSetWindowAttribute(Handle, 34, ref none, 4);    // no Windows border (we draw the rim)
            Native.DwmSetWindowAttribute(Handle, 3, ref off, 4);      // no fade: appears/disappears with the host
            Native.DwmSetWindowAttribute(Handle, 20, ref dark, 4);
            if (Rounded)
            {
                int noRound = 1, noFrame = 1, hostBackdrop = 1;
                Native.DwmSetWindowAttribute(Handle, 33, ref noRound, 4);       // our layers round themselves
                Native.DwmSetWindowAttribute(Handle, 2, ref noFrame, 4);        // no frame/shadow of its own
                Native.DwmSetWindowAttribute(Handle, 17, ref hostBackdrop, 4);  // DWMWA_USE_HOSTBACKDROPBRUSH
            }
            else { int round = 2; Native.DwmSetWindowAttribute(Handle, 33, ref round, 4); }
        }
        catch { }
        if (Rounded && BuildLayers()) return;
        Rounded = false;
        UseAcrylic();
    }

    bool BuildLayers()
    {
        try
        {
            var c = compositor;
            object raw;
            ((ICompositorDesktopInterop)(object)c).CreateDesktopWindowTarget(Handle, true, out raw);
            target = (Windows.UI.Composition.Desktop.DesktopWindowTarget)raw;
            root = c.CreateContainerVisual();
            target.Root = root;

            // Every layer is a vector shape (or masked by one), so its rounded corners are drawn
            // smoothly. (Clips - both the geometric clip and RectangleClip - leave jagged corners.)
            if (!Theme.Glass)
            {
                // Solid look: the opaque Prism panel and its hairline rim. Drawn here too (rather
                // than relying on Windows' fixed 8px window corners) so both looks get the same
                // smooth corners, Rounded or Sharper.
                AddShapes(c);
                AddShapeFill(c.CreateColorBrush(C(Theme.Panel, 1)));
                AddRim(c, Theme.Light ? C(Color.Black, .12) : C(Color.White, .13), 0.5f);
                LayoutGlass();
                return true;
            }
            // 1. the blurred backdrop, cut out by a smooth rounded mask
            // (Windows' mask brush won't take the blur as its source, so a smooth mask isn't possible;
            // trying it failed with an error on every open. The clip below is tucked under the rim.)
            AddFill(c.CreateHostBackdropBrush());
            AddShapes(c);
            // 2. Prism's panel tint (Liquid Glass: the panel thinned right down over the blur)
            Color panel = Theme.Light ? Color.FromArgb(0xf3, 0xf3, 0xf5) : Color.FromArgb(0x14, 0x14, 0x16);
            AddShapeFill(c.CreateColorBrush(C(panel, Theme.Light ? 0.45 : 0.34)));
            // 3. the sheen: a breath of white, brighter at the top (Prism's linear-gradient)
            var sheen = c.CreateLinearGradientBrush();
            sheen.MappingMode = Windows.UI.Composition.CompositionMappingMode.Relative;
            sheen.StartPoint = new System.Numerics.Vector2(0, 0);
            sheen.EndPoint = new System.Numerics.Vector2(0, 1);
            double a0 = Theme.Light ? .42 : .07, a1 = Theme.Light ? .26 : .035, a2 = Theme.Light ? .30 : .05;
            sheen.ColorStops.Add(c.CreateColorGradientStop(0f, C(Color.White, a0)));
            sheen.ColorStops.Add(c.CreateColorGradientStop(.6f, C(Color.White, a1)));
            sheen.ColorStops.Add(c.CreateColorGradientStop(1f, C(Color.White, a2)));
            AddShapeFill(sheen);
            // 4. the rim: one clean hairline all round (Prism's inset 1px rim)
            AddRim(c, C(Color.White, Theme.Light ? .60 : .15), 0.5f);
            LayoutGlass();
            return true;
        }
        catch { return false; }
    }

    // A layer filling the card's rounded shape. Clipped with a RectangleClip, which draws its
    // rounded corners smoothly (anti-aliased); the older geometric clip leaves stair-stepped
    // corners. Falls back to that one only where RectangleClip isn't available.
    readonly List<Windows.UI.Composition.RectangleClip> clips = new List<Windows.UI.Composition.RectangleClip>();

    void AddFill(Windows.UI.Composition.CompositionBrush brush)
    {
        var c = compositor;
        var sprite = c.CreateSpriteVisual();
        sprite.Brush = brush;
        try
        {
            var clip = c.CreateRectangleClip();
            clips.Add(clip);
            sprite.Clip = clip;
        }
        catch
        {
            var g = c.CreateRoundedRectangleGeometry();
            geoms.Add(g);
            sprite.Clip = c.CreateGeometricClip(g);
        }
        root.Children.InsertAtTop(sprite);
        sized.Add(sprite);
    }

    public bool Masked { get { return maskSurface != null; } }   // the blur has the smooth mask (self-test; not used now)

    // One shape layer holds the tint, sheen and rim (or the solid panel and rim).
    Windows.UI.Composition.ShapeVisual shapes;
    Windows.UI.Composition.CompositionVisualSurface maskSurface;

    void AddShapes(Windows.UI.Composition.Compositor c)
    {
        shapes = c.CreateShapeVisual();
        root.Children.InsertAtTop(shapes);
        sized.Add(shapes);
    }

    // a smooth rounded shape filling the card, painted with brush
    void AddShapeFill(Windows.UI.Composition.CompositionBrush brush)
    {
        var g = compositor.CreateRoundedRectangleGeometry();
        geoms.Add(g);
        var s = compositor.CreateSpriteShape(g);
        s.FillBrush = brush;
        shapes.Shapes.Add(s);
    }

    // The blur can't be a shape's paint, so it's masked instead: a white rounded shape is drawn
    // into a surface, and only where that shape is (with its soft, anti-aliased edge) the blur shows.
    bool AddMaskedBackdrop(Windows.UI.Composition.Compositor c)
    {
        try
        {
            var maskVisual = c.CreateShapeVisual();
            var g = c.CreateRoundedRectangleGeometry();
            geoms.Add(g);
            var s = c.CreateSpriteShape(g);
            s.FillBrush = c.CreateColorBrush(Windows.UI.Color.FromArgb(255, 255, 255, 255));
            maskVisual.Shapes.Add(s);
            sized.Add(maskVisual);
            maskSurface = c.CreateVisualSurface();
            maskSurface.SourceVisual = maskVisual;
            var maskBrush = c.CreateSurfaceBrush(maskSurface);
            maskBrush.Stretch = Windows.UI.Composition.CompositionStretch.None;
            var mb = c.CreateMaskBrush();
            mb.Source = c.CreateHostBackdropBrush();
            mb.Mask = maskBrush;
            var sprite = c.CreateSpriteVisual();
            sprite.Brush = mb;
            root.Children.InsertAtTop(sprite);
            sized.Add(sprite);
            return true;
        }
        catch (Exception ex) { HoverWatcher.Log("smooth glass mask unavailable: " + ex.GetType().Name + ": " + ex.Message); maskSurface = null; return false; }
    }

    void AddRim(Windows.UI.Composition.Compositor c, Windows.UI.Color color, float inset)
    {
        var g = c.CreateRoundedRectangleGeometry();
        g.Offset = new System.Numerics.Vector2(inset, inset);
        if (Theme.Glass)
        {
            // a soft line (under the rim) where the blur's unsmoothable edge sits, blending it away
            var g2 = c.CreateRoundedRectangleGeometry();
            g2.Offset = new System.Numerics.Vector2(1.5f, 1.5f);
            var s2 = c.CreateSpriteShape(g2);
            Color panel = Theme.Light ? Color.FromArgb(0xf3, 0xf3, 0xf5) : Color.FromArgb(0x14, 0x14, 0x16);
            s2.StrokeBrush = c.CreateColorBrush(C(panel, Theme.Light ? .55 : .45));
            s2.StrokeThickness = 1.5f;
            shapes.Shapes.Add(s2);
            geoms.Add(g2);
            rimInsets[g2] = 1.5f;
        }
        var shape = c.CreateSpriteShape(g);
        shape.StrokeBrush = c.CreateColorBrush(color);
        shape.StrokeThickness = 1f;
        shapes.Shapes.Add(shape);
        geoms.Add(g);
        rimInsets[g] = inset;
    }
    readonly Dictionary<Windows.UI.Composition.CompositionRoundedRectangleGeometry, float> rimInsets = new Dictionary<Windows.UI.Composition.CompositionRoundedRectangleGeometry, float>();

    void LayoutGlass()
    {
        if (root == null) return;
        float w = Width, h = Height, r = radius * FolderForm.S;
        var size = new System.Numerics.Vector2(w, h);
        root.Size = size;
        foreach (var v in sized) v.Size = size;
        // The blur's clip can't be smooth, so its edge is tucked just inside the card and hidden under
        // the smooth rim; the card's outline itself comes from the smooth shapes on top.
        float ins = 1.5f;
        var rr = new System.Numerics.Vector2(r - ins, r - ins);
        foreach (var cl in clips)
        {
            cl.Left = ins; cl.Top = ins; cl.Right = w - ins; cl.Bottom = h - ins;
            cl.TopLeftRadius = rr; cl.TopRightRadius = rr; cl.BottomRightRadius = rr; cl.BottomLeftRadius = rr;
        }
        foreach (var g in geoms)
        {
            float inset;
            if (rimInsets.TryGetValue(g, out inset))
            {
                g.Size = new System.Numerics.Vector2(w - 2 * inset, h - 2 * inset);
                g.CornerRadius = new System.Numerics.Vector2(r - inset, r - inset);
            }
            else
            {
                g.Size = size;
                g.CornerRadius = new System.Numerics.Vector2(r, r);
            }
        }
        if (maskSurface != null) maskSurface.SourceSize = size;
        // No window region: a region is hard-edged (it could only ever make corners rougher).
        // The few pixels just outside each rounded corner count as part of the folder for clicks.
    }

    protected override void OnSizeChanged(EventArgs e) { base.OnSizeChanged(e); LayoutGlass(); }

    void UseAcrylic()
    {
        if (!Theme.Glass) { BackColor = Theme.Panel; return; }   // Solid look without composition: just the panel
        // ACCENT_ENABLE_ACRYLICBLURBEHIND with the Prism panel colour as the tint
        var accent = new AccentPolicy { State = 4, Flags = 2, Gradient = Theme.GlassTint };
        int size = Marshal.SizeOf(accent);
        IntPtr p = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(accent, p, false);
            var data = new CompositionData { Attribute = 19 /*WCA_ACCENT_POLICY*/, Data = p, Size = size };
            if (SetWindowCompositionAttribute(Handle, ref data) == 0) BackColor = Theme.Panel;   // no blur available: solid
        }
        catch { BackColor = Theme.Panel; }
        finally { Marshal.FreeHGlobal(p); }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            try { if (target != null) { target.Root = null; target.Dispose(); } } catch { }
            target = null; root = null;
        }
        base.Dispose(disposing);
    }
}

// Background helper (starts with Windows): when the pointer rests on an App Folder on the
// Desktop for a moment, opens it. Reads which Desktop icon is under the pointer straight from the
// Desktop's icon list (UI Automation can't see those icons on current Windows 11 builds).
static class HoverWatcher
{
    [DllImport("user32.dll")] static extern bool ScreenToClient(IntPtr h, ref Point p);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr h, uint msg, IntPtr w, IntPtr l);
    [DllImport("kernel32.dll")] static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);
    [DllImport("kernel32.dll")] static extern IntPtr VirtualAllocEx(IntPtr proc, IntPtr addr, UIntPtr size, uint type, uint protect);
    [DllImport("kernel32.dll")] static extern bool WriteProcessMemory(IntPtr proc, IntPtr addr, byte[] buf, UIntPtr size, out UIntPtr written);
    [DllImport("kernel32.dll")] static extern bool ReadProcessMemory(IntPtr proc, IntPtr addr, byte[] buf, UIntPtr size, out UIntPtr read);

    // A small buffer inside Explorer, reused for every lookup (Explorer owns the icon list's memory).
    static IntPtr listView, proc, remote;

    static void Attach(IntPtr lv)
    {
        if (lv == listView && proc != IntPtr.Zero) return;
        if (proc != IntPtr.Zero) CloseHandle(proc);   // Explorer restarted: the old buffer died with it
        listView = lv; proc = IntPtr.Zero; remote = IntPtr.Zero;
        uint pid;
        GetWindowThreadProcessId(lv, out pid);
        proc = OpenProcess(0x0008 | 0x0010 | 0x0020, false, pid);   // VM_OPERATION | VM_READ | VM_WRITE
        if (proc != IntPtr.Zero) remote = VirtualAllocEx(proc, IntPtr.Zero, (UIntPtr)4096, 0x3000, 0x04);
    }

    // Name of the Desktop icon under screen point p, or null.
    static string IconNameAt(IntPtr lv, Point p)
    {
        Attach(lv);
        if (remote == IntPtr.Zero) return null;
        ScreenToClient(lv, ref p);
        UIntPtr n;
        var hit = new byte[24];   // LVHITTESTINFO
        BitConverter.GetBytes(p.X).CopyTo(hit, 0);
        BitConverter.GetBytes(p.Y).CopyTo(hit, 4);
        if (!WriteProcessMemory(proc, remote, hit, (UIntPtr)hit.Length, out n)) { listView = IntPtr.Zero; return null; }
        int index = (int)SendMessage(lv, 0x1012 /*LVM_HITTEST*/, IntPtr.Zero, remote);
        if (index < 0) return null;
        ReadProcessMemory(proc, remote, hit, (UIntPtr)hit.Length, out n);
        uint flags = BitConverter.ToUInt32(hit, 8);
        if ((flags & 0x0E) == 0) return null;   // not on the icon or its label

        var item = new byte[88];  // LVITEMW (64-bit layout): iSubItem @8, pszText @24, cchTextMax @32
        IntPtr text = remote + 512;
        BitConverter.GetBytes(text.ToInt64()).CopyTo(item, 24);
        BitConverter.GetBytes(260).CopyTo(item, 32);
        WriteProcessMemory(proc, remote + 128, item, (UIntPtr)item.Length, out n);
        int len = (int)SendMessage(lv, 0x1073 /*LVM_GETITEMTEXTW*/, (IntPtr)index, remote + 128);
        if (len <= 0) return null;
        var buf = new byte[Math.Min(len, 260) * 2];
        ReadProcessMemory(proc, text, buf, (UIntPtr)buf.Length, out n);
        return Encoding.Unicode.GetString(buf);
    }

    const int DwellMs = 0;   // no wait: a folder opens as soon as the pointer is on it

    // Right after start-up, do once what the first open would otherwise do: start Windows'
    // compositor, load every Desktop folder's icons, and run the folder window's code once
    // (without showing it), so even the first hover opens instantly.
    static void WarmUp()
    {
        try { GlassBackdrop.Warm(); } catch { }
        try { Theme.Load(); FolderForm.RefreshAllIcons(); } catch { }   // e.g. Windows went light/dark while signed out
        try
        {
            foreach (var dir in new[] { Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                                        Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory) })
            {
                if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) continue;
                foreach (var f in Directory.GetFiles(dir, "*" + Installer.Ext))
                    using (new FolderForm("warmup", f)) { }   // loads and caches its icons
            }
        }
        catch { }
    }

    public static bool Enabled { get { return Settings.HoverOpen; } }

    // Folders this helper has open, by file. Opening them right here (instead of starting a new
    // AppFolder process each time) is what makes hover-open feel instant.
    static readonly Dictionary<string, FolderForm> open = new Dictionary<string, FolderForm>(StringComparer.OrdinalIgnoreCase);

    const int SpringMs = 450;   // while dragging: pause this long on a folder and it springs open

    static void OpenFolder(string file, bool spring = false)
    {
        if (open.ContainsKey(file)) return;
        string id = "file:" + Path.GetFullPath(file).ToLowerInvariant().Replace('\\', '/');
        bool created;
        Mutex mutex;
        try { mutex = new Mutex(true, "AppFolder_" + id, out created); }   // same lock as a double-clicked folder
        catch (AbandonedMutexException) { return; }
        if (!created) { mutex.Dispose(); return; }                     // already open in another window
        bool released = false;
        Action release = () =>
        {
            if (released) return;
            released = true;
            open.Remove(file);
            try { mutex.ReleaseMutex(); } catch { }
            mutex.Dispose();
        };
        FolderForm f = null;
        try
        {
            Theme.Load();                                              // pick up a changed look
            FolderForm.StartRenaming = false;
            f = new FolderForm(id, file) { HoverMode = true, SpringMode = spring };
            open[file] = f;
            f.FormClosed += (s, e) => release();
            f.Show();
        }
        catch (Exception ex)
        {
            // never leave the lock (or the "open" entry) behind: that folder would refuse to open until a restart
            Log("couldn't open " + file + ": " + ex.Message);
            release();
            if (f != null) try { f.Dispose(); } catch { }
        }
    }

    [DllImport("user32.dll")] static extern IntPtr WindowFromPoint(Point p);
    [DllImport("user32.dll")] static extern IntPtr GetParent(IntPtr h);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr h, StringBuilder sb, int max);
    [DllImport("user32.dll")] static extern short GetAsyncKeyState(int vk);

    static string ClassOf(IntPtr h) { var sb = new StringBuilder(64); GetClassName(h, sb, 64); return sb.ToString(); }

    // The Desktop's icon view is a SysListView32 inside SHELLDLL_DefView inside Progman/WorkerW.
    static IntPtr DesktopIconsAt(Point p)
    {
        IntPtr h = WindowFromPoint(p);
        if (h == IntPtr.Zero || ClassOf(h) != "SysListView32") return IntPtr.Zero;
        IntPtr view = GetParent(h);
        if (view == IntPtr.Zero || ClassOf(view) != "SHELLDLL_DefView") return IntPtr.Zero;
        string top = ClassOf(GetParent(view));
        return top == "Progman" || top == "WorkerW" ? h : IntPtr.Zero;
    }

    // The pointer check runs 40 times a second; resting on one icon shouldn't mean checking the
    // disk 40 times a second, so the answer for the last name is kept for a moment.
    static string lastName, lastFile;
    static DateTime lastLookup;

    static string FolderFileFor(string name)
    {
        if (string.IsNullOrEmpty(name)) return null;
        if (name == lastName && (DateTime.Now - lastLookup).TotalMilliseconds < 1000) return lastFile;
        lastFile = LookUpFolderFile(name);
        lastName = name; lastLookup = DateTime.Now;
        return lastFile;
    }

    static string LookUpFolderFile(string name)
    {
        foreach (var dir in new[] { Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                                    Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory) })
        {
            if (string.IsNullOrEmpty(dir)) continue;
            string f = Path.Combine(dir, name + Installer.Ext);
            if (File.Exists(f)) return f;
            if (name.EndsWith(Installer.Ext, StringComparison.OrdinalIgnoreCase) && File.Exists(Path.Combine(dir, name))) return Path.Combine(dir, name);
        }
        return null;
    }

    [DllImport("user32.dll")] static extern bool ClientToScreen(IntPtr h, ref Point p);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string cls, string title);

    // Self-test (AppFolder.exe --probe out.txt): runs the hover lookup on every Desktop icon's position.
    public static void Probe(string outFile)
    {
        var sb = new StringBuilder();
        IntPtr lv = IntPtr.Zero;
        IntPtr top = IntPtr.Zero;
        foreach (string cls in new[] { "Progman", "WorkerW" })
        {
            top = IntPtr.Zero;
            while ((top = FindWindowEx(IntPtr.Zero, top, cls, null)) != IntPtr.Zero && lv == IntPtr.Zero)
            {
                IntPtr dv = FindWindowEx(top, IntPtr.Zero, "SHELLDLL_DefView", null);
                if (dv != IntPtr.Zero) lv = FindWindowEx(dv, IntPtr.Zero, "SysListView32", null);
            }
            if (lv != IntPtr.Zero) break;
        }
        sb.AppendLine("desktop icon list: " + lv);
        if (lv != IntPtr.Zero)
        {
            int count = (int)SendMessage(lv, 0x1004 /*LVM_GETITEMCOUNT*/, IntPtr.Zero, IntPtr.Zero);
            sb.AppendLine("icons: " + count);
            Attach(lv);
            for (int i = 0; i < count; i++)
            {
                UIntPtr n;
                SendMessage(lv, 0x1010 /*LVM_GETITEMPOSITION*/, (IntPtr)i, remote + 1024);
                var pos = new byte[8];
                ReadProcessMemory(proc, remote + 1024, pos, (UIntPtr)8, out n);
                var pt = new Point(BitConverter.ToInt32(pos, 0) + 20, BitConverter.ToInt32(pos, 4) + 15);
                ClientToScreen(lv, ref pt);
                string name = IconNameAt(lv, pt);
                string file = FolderFileFor(name);
                if (i < 6 || file != null)
                    sb.AppendLine("  #" + i + " at " + pt + " -> '" + name + "'" + (file != null ? "   <-- App Folder: would open " + file : ""));
            }
        }
        File.WriteAllText(outFile, sb.ToString());
    }

    // %TEMP%\PrysmFolder.log: when the helper starts/stops, and any errors (kept small)
    public static void Log(string text)
    {
        try
        {
            string f = Path.Combine(Path.GetTempPath(), "PrysmFolder.log");
            if (File.Exists(f) && new FileInfo(f).Length > 512 * 1024) File.Delete(f);
            File.AppendAllText(f, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + text + "\r\n");
        }
        catch { }
    }

    public static bool IsRunning
    {
        get
        {
            Mutex m;
            if (!Mutex.TryOpenExisting("AppFolder_HoverWatcher", out m)) return false;
            m.Dispose();
            return true;
        }
    }

    // Starts the hover helper as its own independent process (via WMI), so it isn't tied to
    // whatever started it - e.g. a terminal that closes everything it launched when it ends.
    public static void StartDetached()
    {
        if (IsRunning || !File.Exists(Installer.Exe)) return;
        try
        {
            using (var cls = new System.Management.ManagementClass("Win32_Process"))
            {
                var inParams = cls.GetMethodParameters("Create");
                inParams["CommandLine"] = "\"" + Installer.Exe + "\" --watch";
                inParams["CurrentDirectory"] = Installer.Dir;
                var outParams = cls.InvokeMethod("Create", inParams, null);
                if (Convert.ToInt32(outParams["ReturnValue"]) == 0) return;
            }
        }
        catch { }
        try { Process.Start(new ProcessStartInfo(Installer.Exe, "--watch") { UseShellExecute = false }); } catch { }
    }

    public static void Run()
    {
        bool created;
        using (new Mutex(true, "AppFolder_HoverWatcher", out created))
        {
            if (!created) return;
            Log("hover helper started (pid " + Process.GetCurrentProcess().Id + ")");
            Application.ApplicationExit += (s, e) => Log("hover helper stopped");
            AppDomain.CurrentDomain.UnhandledException += (s, e) => Log("hover helper crashed: " + e.ExceptionObject);
            // a problem in one folder window must never take the hover helper down (but note it down)
            Application.ThreadException += (s, e) => Log("error: " + e.Exception);
            // Windows switched light/dark: redraw the Desktop icons to match
            Microsoft.Win32.SystemEvents.UserPreferenceChanged += (s, e) =>
            {
                if (e.Category != UserPreferenceCategory.General) return;
                bool wasLight = Theme.Light;
                Theme.Load();
                if (Theme.Light != wasLight) FolderForm.RefreshAllIcons();
            };
            var warm = new System.Windows.Forms.Timer { Interval = 1500 };
            warm.Tick += (s, e) => { warm.Dispose(); WarmUp(); };
            warm.Start();
            // updates: a quiet check 30 s after start, then every 6 h (actually asking GitHub at most once a day)
            var updateUi = new Control();
            updateUi.CreateControl();
            var updates = new System.Windows.Forms.Timer { Interval = 30 * 1000 };
            updates.Tick += (s, e) => { updates.Interval = 6 * 60 * 60 * 1000; Updater.BackgroundCheck(updateUi); };
            updates.Start();
            string candidate = null, opened = null;
            DateTime since = DateTime.MinValue;
            int idleChecks = 0;
            // Spring-loading: while you drag something (an app, a file, a folder) and pause on a
            // folder's icon, the folder springs open so you can drop it inside. Not when the thing
            // being dragged is that folder's own icon (you're just moving it).
            bool wasDown = false, dragging = false;
            Point pressAt = Point.Empty;
            string pressedOn = null;
            Application.AddMessageFilter(new InteractionFilter());
            var timer = new System.Windows.Forms.Timer { Interval = 25 };
            timer.Tick += (s, e) =>
            {
                if (++idleChecks % 80 == 0 && !Installer.IsInstalled) { Log("not installed any more: stopping"); Application.Exit(); return; }
                Point p = Cursor.Position;
                bool left = GetAsyncKeyState(0x01) < 0, right = GetAsyncKeyState(0x02) < 0;
                IntPtr lv = right ? IntPtr.Zero : DesktopIconsAt(p);

                if (left && !wasDown)
                {
                    // a press just started: remember where, and on which Desktop icon (if any)
                    pressAt = p; dragging = false; pressedOn = null;
                    if (lv != IntPtr.Zero) try { pressedOn = FolderFileFor(IconNameAt(lv, p)); } catch { }
                }
                wasDown = left;
                if (left && !dragging)
                {
                    var d = SystemInformation.DragSize;
                    dragging = Math.Abs(p.X - pressAt.X) > d.Width || Math.Abs(p.Y - pressAt.Y) > d.Height;
                }
                if (left && !dragging) { candidate = null; return; }   // a plain click: leave it to Explorer

                if (lv == IntPtr.Zero || !Enabled) { candidate = null; opened = null; return; }
                string name = null;
                try { name = IconNameAt(lv, p); } catch { }
                string file = FolderFileFor(name);
                if (file == null) { candidate = null; opened = null; return; }
                if (left && string.Equals(file, pressedOn, StringComparison.OrdinalIgnoreCase)) return;   // moving this folder's own icon
                if (file == opened) return;                         // already opened for this hover; move off to re-arm
                int wait = left ? SpringMs : DwellMs;
                if (file != candidate) { candidate = file; since = DateTime.Now; if (wait > 0) return; }
                if ((DateTime.Now - since).TotalMilliseconds < wait) return;
                opened = file;
                try { OpenFolder(file, left); } catch { }
            };
            timer.Start();
            Application.Run();
        }
    }
}

// Installs for the current user: copies the exe to %LOCALAPPDATA%\Programs\Prysm Folder and
// registers the .appfolder file type so "Prysm Folder" shows up under right-click > New.
// (Internal names - the .appfolder extension, settings key, file type id - stay as they were
// so folders made before the rename keep working.)
static class Installer
{
    public const string AppName = "Prysm Folder";
    public const string Ext = ".appfolder";
    const string ProgId = "AppFolder.Folder";
    const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\PrysmFolder";
    const string OldUninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\AppFolder";
    const string RunValue = "Prysm Folder", OldRunValue = "AppFolder";
    static string OldDir { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "AppFolder"); } }
    static readonly string[] BackgroundKeys = {
        @"Software\Classes\DesktopBackground\Shell\AppFolder.New",
        @"Software\Classes\Directory\Background\shell\AppFolder.New" };

    // Makes "New Prysm Folder.appfolder" (or "... (2)") in dir and returns its path.
    public static string CreateFolderFile(string dir, string name)
    {
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
            dir = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        string baseName = FolderForm.SafeFileName(name), f = Path.Combine(dir, baseName + Ext);
        int n = 2;
        while (File.Exists(f)) f = Path.Combine(dir, baseName + " (" + n++ + ")" + Ext);
        FolderForm.WriteFolderFile(f, FolderForm.RenderPreview(new List<Item>(), 256), "pinned=0\r\n");
        Native.NotifyFileChanged(f);
        return f;
    }

    public static string Dir
    {
        get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", Installer.AppName);}
    }
    public static string Exe { get { return Path.Combine(Dir, AppName + ".exe"); } }

    // The empty folder that New > Prysm Folder copies, drawn in the current look.
    public static void WriteTemplate()
    {
        if (!Directory.Exists(Dir)) return;
        FolderForm.WriteFolderFile(Path.Combine(Dir, "Template" + Ext), FolderForm.RenderPreview(new List<Item>(), 256), "iconlook=" + Theme.LookSig + "\r\n");
    }
    static string Ico { get { return Path.Combine(Dir, AppName + ".ico"); } }

    public static bool IsInstalled
    {
        get { using (var k = Registry.CurrentUser.OpenSubKey(@"Software\Classes\" + ProgId)) return k != null && File.Exists(Exe); }
    }

    public static bool RunningInstalled
    {
        get { return string.Equals(Path.GetFullPath(Application.ExecutablePath), Path.GetFullPath(Exe), StringComparison.OrdinalIgnoreCase); }
    }

    public static void Install(bool quiet)
    {
        try
        {
            bool firstTime = !IsInstalled;
            Directory.CreateDirectory(Dir);
            StopOtherCopies();   // the hover helper / open folders lock the exe
            if (!RunningInstalled) File.Copy(Application.ExecutablePath, Exe, true);
            bool movedFromOldName = RemoveOldInstall();

            string template = Path.Combine(Dir, "Template" + Ext);
            WriteTemplate();
            string ico = Ico;
            FolderForm.WriteIco(ico, FolderForm.RenderPreview(new List<Item>(), 256));

            using (var cls = Registry.CurrentUser.CreateSubKey(@"Software\Classes"))
            {
                using (var k = cls.CreateSubKey(Ext))
                {
                    k.SetValue("", ProgId);
                    // Registered in both places Explorer looks (.ext\ShellNew and .ext\ProgId\ShellNew).
                    foreach (string sub in new[] { "ShellNew", ProgId + @"\ShellNew" })
                        using (var sn = k.CreateSubKey(sub))
                        {
                            sn.SetValue("FileName", template);
                            sn.SetValue("ItemName", "New " + AppName);
                            sn.SetValue("IconPath", ico);
                        }
                }
                using (var k = cls.CreateSubKey(ProgId))
                {
                    k.SetValue("", AppName);           // the text shown in the New menu
                    k.SetValue("FriendlyTypeName", AppName);
                    k.SetValue("NeverShowExt", "");         // looks like "Games", not "Games.appfolder"
                    using (var d = k.CreateSubKey("DefaultIcon")) d.SetValue("", "%1");   // icon = the file's own live preview
                    using (var c = k.CreateSubKey(@"shell\open\command")) c.SetValue("", "\"" + Exe + "\" \"%1\" %*");
                    // Dropping files on a folder's icon runs "open" with the dropped files after it (Windows' own drop handler for programs/scripts)
                    using (var dh = k.CreateSubKey(@"shellex\DropHandler")) dh.SetValue("", "{86C86720-42A0-1069-A2E8-08002B30309D}");
                }
            }
            // Top-level "New Prysm Folder" item when right-clicking the Desktop or a folder's empty space.
            foreach (string bg in BackgroundKeys)
                using (var k = Registry.CurrentUser.CreateSubKey(bg))
                {
                    k.SetValue("MUIVerb", "New " + AppName);
                    k.SetValue("Icon", ico);
                    k.SetValue("Position", "Top");
                    using (var c = k.CreateSubKey("command")) c.SetValue("", "\"" + Exe + "\" --new \"%V\"");
                }
            using (var u = Registry.CurrentUser.CreateSubKey(UninstallKey))
            {
                u.SetValue("DisplayName", Installer.AppName);
                u.SetValue("DisplayIcon", ico);
                u.SetValue("Publisher", Installer.AppName);
                u.SetValue("InstallLocation", Dir);
                u.SetValue("UninstallString", "\"" + Exe + "\" --uninstall");
                u.SetValue("NoModify", 1, RegistryValueKind.DWord);
                u.SetValue("NoRepair", 1, RegistryValueKind.DWord);
            }
            // Hover-to-open helper starts with Windows.
            using (var run = Registry.CurrentUser.CreateSubKey(RunKey)) run.SetValue(RunValue, "\"" + Exe + "\" --watch");
            HoverWatcher.StartDetached();

            // Windows 11's new right-click menu only lists New items registered for the whole PC,
            // so ask (one UAC prompt) to add the same New entry machine-wide, unless it's already there.
            bool machineOk;
            using (var k = Registry.LocalMachine.OpenSubKey(@"Software\Classes\" + Ext + @"\ShellNew"))
                machineOk = k != null && string.Equals(k.GetValue("FileName") as string, template, StringComparison.OrdinalIgnoreCase)
                            && string.Equals(k.GetValue("ItemName") as string, "New " + AppName);
            if (!machineOk) RunElevated("--machine-register");
            RefreshShell();
            if (firstTime || !machineOk || movedFromOldName) RestartExplorer();
            if (!quiet)
                MessageBox.Show(AppName + " is installed.\n\nRight-click the Desktop (or inside any folder) and choose\nNew > " + AppName + ", then give it a name.\n\nPoint at it (or double-click it) to open it, and drag apps in.\nUninstall any time from Settings > Apps.",
                    Installer.AppName, MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show("Install failed:\n" + ex.Message, Installer.AppName, MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    public static void Uninstall(bool quiet)
    {
        if (!quiet && MessageBox.Show("Uninstall " + AppName + "?\n\nYour folders on the Desktop are kept, but won't open until you install it again.",
                Installer.AppName, MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
        try { Registry.CurrentUser.DeleteSubKeyTree(@"Software\Classes\" + Ext, false); } catch { }
        try { Registry.CurrentUser.DeleteSubKeyTree(@"Software\Classes\" + ProgId, false); } catch { }
        try { Registry.CurrentUser.DeleteSubKeyTree(UninstallKey, false); } catch { }
        foreach (string bg in BackgroundKeys)
            try { Registry.CurrentUser.DeleteSubKeyTree(bg, false); } catch { }
        try { using (var run = Registry.CurrentUser.OpenSubKey(RunKey, true)) if (run != null) { run.DeleteValue(RunValue, false); run.DeleteValue(OldRunValue, false); } } catch { }
        try { Registry.CurrentUser.DeleteSubKeyTree(OldUninstallKey, false); } catch { }
        try { Registry.CurrentUser.DeleteSubKeyTree(@"Software\AppFolder", false); } catch { }
        StopOtherCopies();
        using (var k = Registry.LocalMachine.OpenSubKey(@"Software\Classes\" + ProgId))
            if (k != null) RunElevated("--machine-unregister");
        RefreshShell();
        RestartExplorer();
        // Remove the install folder once this process has exited.
        try
        {
            Process.Start(new ProcessStartInfo("cmd.exe", "/c ping 127.0.0.1 -n 3 > nul & rmdir /s /q \"" + Dir + "\"")
            { CreateNoWindow = true, UseShellExecute = false });
        }
        catch { }
        if (!quiet) MessageBox.Show(AppName + " was uninstalled.", Installer.AppName);
    }

    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    // Before the rename this was "AppFolder" in %LOCALAPPDATA%\Programs\AppFolder: move off it.
    static bool RemoveOldInstall()
    {
        bool any = false;
        try { using (var run = Registry.CurrentUser.OpenSubKey(RunKey, true)) if (run != null && run.GetValue(OldRunValue) != null) { run.DeleteValue(OldRunValue, false); any = true; } } catch { }
        try { using (var k = Registry.CurrentUser.OpenSubKey(OldUninstallKey)) if (k != null) any = true; Registry.CurrentUser.DeleteSubKeyTree(OldUninstallKey, false); } catch { }
        if (Directory.Exists(OldDir) && !string.Equals(Path.GetFullPath(OldDir), Path.GetFullPath(Dir), StringComparison.OrdinalIgnoreCase))
        {
            any = true;
            try
            {
                Process.Start(new ProcessStartInfo("cmd.exe", "/c ping 127.0.0.1 -n 3 > nul & rmdir /s /q \"" + OldDir + "\"")
                { CreateNoWindow = true, UseShellExecute = false });
            }
            catch { }
        }
        return any;
    }

    // Ends the hover helper and any open folders from the install folder (old name or new).
    static void StopOtherCopies()
    {
        int me = Process.GetCurrentProcess().Id;
        foreach (string name in new[] { AppName, "AppFolder" })
            foreach (var p in Process.GetProcessesByName(name))
            {
                try
                {
                    if (p.Id == me) continue;
                    string f = p.MainModule.FileName;
                    if (f.StartsWith(Dir + "\\", StringComparison.OrdinalIgnoreCase) || f.StartsWith(OldDir + "\\", StringComparison.OrdinalIgnoreCase))
                    { p.Kill(); p.WaitForExit(3000); }
                }
                catch { }
            }
    }

    static void RunElevated(string arg)
    {
        try
        {
            var p = Process.Start(new ProcessStartInfo(Exe, arg) { UseShellExecute = true, Verb = "runas" });
            if (p != null) p.WaitForExit(30000);
        }
        catch { }   // user said No to UAC: per-user install still works (New menu via "Show more options")
    }

    // Runs elevated: the machine-wide part of the New menu entry. Points at the per-user install,
    // so it only does anything for the user who installed it.
    public static void MachineRegister()
    {
        string template = Path.Combine(Dir, "Template" + Ext);
        string ico = Ico;
        using (var cls = Registry.LocalMachine.CreateSubKey(@"Software\Classes"))
        {
            using (var k = cls.CreateSubKey(Ext))
            {
                k.SetValue("", ProgId);
                foreach (string sub in new[] { "ShellNew", ProgId + @"\ShellNew" })
                    using (var sn = k.CreateSubKey(sub))
                    {
                        sn.SetValue("FileName", template);
                        sn.SetValue("ItemName", "New " + AppName);
                        sn.SetValue("IconPath", ico);
                    }
            }
            using (var k = cls.CreateSubKey(ProgId))
            {
                k.SetValue("", AppName);
                k.SetValue("FriendlyTypeName", AppName);
                k.SetValue("NeverShowExt", "");
                using (var d = k.CreateSubKey("DefaultIcon")) d.SetValue("", "%1");
                using (var c = k.CreateSubKey(@"shell\open\command")) c.SetValue("", "\"" + Exe + "\" \"%1\" %*");
                    // Dropping files on a folder's icon runs "open" with the dropped files after it (Windows' own drop handler for programs/scripts)
                    using (var dh = k.CreateSubKey(@"shellex\DropHandler")) dh.SetValue("", "{86C86720-42A0-1069-A2E8-08002B30309D}");
            }
        }
    }

    public static void MachineUnregister()
    {
        try { Registry.LocalMachine.DeleteSubKeyTree(@"Software\Classes\" + Ext, false); } catch { }
        try { Registry.LocalMachine.DeleteSubKeyTree(@"Software\Classes\" + ProgId, false); } catch { }
    }

    // Explorer only reads the New menu list when it starts, so restart it (the Desktop and taskbar
    // blink for a second; open File Explorer windows close).
    static void RestartExplorer()
    {
        try
        {
            foreach (var p in Process.GetProcessesByName("explorer"))
            {
                try { p.Kill(); p.WaitForExit(5000); } catch { }
            }
            Thread.Sleep(1500);
            if (Process.GetProcessesByName("explorer").Length == 0)
                Process.Start(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe"));
        }
        catch { }
    }

    static void RefreshShell()
    {
        // Explorer caches the New menu; clearing the cache makes it rebuild with our entry.
        try { Registry.CurrentUser.DeleteSubKeyTree(@"Software\Microsoft\Windows\CurrentVersion\Explorer\Discardable\PostSetup\ShellNew", false); } catch { }
        Native.SHChangeNotify(0x08000000 /*SHCNE_ASSOCCHANGED*/, 0, IntPtr.Zero, IntPtr.Zero);
    }
}

// An animated PNG (APNG) writer for the README demo: full colour (no palette, no dithering), so
// glass and motion stay smooth, and browsers/GitHub play and loop it like a GIF. Frames are
// streamed: each one stores only the rectangle that changed, with unchanged pixels transparent.
class ApngWriter : IDisposable
{
    readonly Stream fs;
    readonly int w, h, frameCount, delayNum, delayDen;
    int seq, written;
    int[] prev;
    static uint[] crcTable;

    public ApngWriter(string path, int width, int height, int frames, int delayNum, int delayDen)
    {
        w = width; h = height; frameCount = frames; this.delayNum = delayNum; this.delayDen = delayDen;
        fs = File.Create(path);
        fs.Write(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }, 0, 8);
        var ihdr = new MemoryStream();
        BE(ihdr, w); BE(ihdr, h);
        ihdr.WriteByte(8); ihdr.WriteByte(6); ihdr.WriteByte(0); ihdr.WriteByte(0); ihdr.WriteByte(0);   // 8-bit RGBA
        Chunk("IHDR", ihdr.ToArray());
        var actl = new MemoryStream();
        BE(actl, frameCount); BE(actl, 0);   // loop forever
        Chunk("acTL", actl.ToArray());
    }

    // argb: one int per pixel (0xAARRGGBB), the whole canvas
    public void AddFrame(int[] argb)
    {
        int x0 = 0, y0 = 0, x1 = w - 1, y1 = h - 1;
        if (prev != null)
        {
            x0 = w; y0 = h; x1 = -1; y1 = -1;
            for (int y = 0; y < h; y++)
            {
                int row = y * w;
                for (int x = 0; x < w; x++)
                    if (argb[row + x] != prev[row + x]) { if (x < x0) x0 = x; if (x > x1) x1 = x; if (y < y0) y0 = y; if (y > y1) y1 = y; }
            }
            if (x1 < 0) { x0 = y0 = x1 = y1 = 0; }
        }
        int rw = x1 - x0 + 1, rh = y1 - y0 + 1;

        // raw RGBA scanlines of the rectangle (unchanged pixels fully transparent), each row filtered
        int stride = rw * 4;
        var raw = new byte[rh * (stride + 1)];
        var cur = new byte[stride];
        var up = new byte[stride];
        var best = new byte[stride];
        var tryBuf = new byte[stride];
        for (int y = 0; y < rh; y++)
        {
            for (int x = 0; x < rw; x++)
            {
                int i = (y0 + y) * w + x0 + x, c = argb[i];
                bool same = prev != null && prev[i] == c;
                int o = x * 4;
                cur[o] = same ? (byte)0 : (byte)(c >> 16);
                cur[o + 1] = same ? (byte)0 : (byte)(c >> 8);
                cur[o + 2] = same ? (byte)0 : (byte)c;
                cur[o + 3] = same ? (byte)0 : (byte)255;
            }
            int bestType = 0; long bestSum = long.MaxValue;
            for (int ft = 0; ft < 5; ft++)
            {
                long sum = 0;
                for (int i = 0; i < stride; i++)
                {
                    int a = i >= 4 ? cur[i - 4] : 0, b = y > 0 ? up[i] : 0, cc = (i >= 4 && y > 0) ? up[i - 4] : 0, v;
                    switch (ft)
                    {
                        case 0: v = cur[i]; break;
                        case 1: v = cur[i] - a; break;
                        case 2: v = cur[i] - b; break;
                        case 3: v = cur[i] - ((a + b) >> 1); break;
                        default: v = cur[i] - Paeth(a, b, cc); break;
                    }
                    tryBuf[i] = (byte)v;
                    int sv = (sbyte)(byte)v;
                    sum += sv < 0 ? -sv : sv;
                }
                if (sum < bestSum) { bestSum = sum; bestType = ft; Buffer.BlockCopy(tryBuf, 0, best, 0, stride); }
            }
            raw[y * (stride + 1)] = (byte)bestType;
            Buffer.BlockCopy(best, 0, raw, y * (stride + 1) + 1, stride);
            Buffer.BlockCopy(cur, 0, up, 0, stride);
        }
        byte[] z = Zlib(raw);

        var fctl = new MemoryStream();
        BE(fctl, seq++); BE(fctl, rw); BE(fctl, rh); BE(fctl, x0); BE(fctl, y0);
        BE16(fctl, delayNum); BE16(fctl, delayDen);
        fctl.WriteByte(0);                        // dispose: leave it
        fctl.WriteByte((byte)(prev == null ? 0 : 1));   // blend: first frame replaces, the rest draw over
        Chunk("fcTL", fctl.ToArray());
        if (written == 0) Chunk("IDAT", z);
        else
        {
            var fd = new MemoryStream();
            BE(fd, seq++);
            fd.Write(z, 0, z.Length);
            Chunk("fdAT", fd.ToArray());
        }
        written++;
        prev = argb;
    }

    public void Dispose()
    {
        Chunk("IEND", new byte[0]);
        fs.Dispose();
    }

    static int Paeth(int a, int b, int c)
    {
        int p = a + b - c, pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c);
        return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
    }

    static byte[] Zlib(byte[] data)
    {
        var ms = new MemoryStream();
        ms.WriteByte(0x78); ms.WriteByte(0xDA);
        using (var ds = new System.IO.Compression.DeflateStream(ms, System.IO.Compression.CompressionLevel.Optimal, true))
            ds.Write(data, 0, data.Length);
        uint a = 1, b = 0;
        foreach (byte x in data) { a = (a + x) % 65521; b = (b + a) % 65521; }
        uint adler = (b << 16) | a;
        ms.WriteByte((byte)(adler >> 24)); ms.WriteByte((byte)(adler >> 16)); ms.WriteByte((byte)(adler >> 8)); ms.WriteByte((byte)adler);
        return ms.ToArray();
    }

    void Chunk(string type, byte[] data)
    {
        var t = Encoding.ASCII.GetBytes(type);
        var len = new byte[] { (byte)(data.Length >> 24), (byte)(data.Length >> 16), (byte)(data.Length >> 8), (byte)data.Length };
        fs.Write(len, 0, 4);
        fs.Write(t, 0, 4);
        fs.Write(data, 0, data.Length);
        uint crc = Crc(Crc(0xFFFFFFFF, t), data) ^ 0xFFFFFFFF;
        fs.Write(new byte[] { (byte)(crc >> 24), (byte)(crc >> 16), (byte)(crc >> 8), (byte)crc }, 0, 4);
    }

    static uint Crc(uint crc, byte[] buf)
    {
        if (crcTable == null)
        {
            crcTable = new uint[256];
            for (uint n = 0; n < 256; n++)
            {
                uint c = n;
                for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
                crcTable[n] = c;
            }
        }
        foreach (byte x in buf) crc = crcTable[(crc ^ x) & 0xFF] ^ (crc >> 8);
        return crc;
    }

    static void BE(Stream s, int v) { s.WriteByte((byte)(v >> 24)); s.WriteByte((byte)(v >> 16)); s.WriteByte((byte)(v >> 8)); s.WriteByte((byte)v); }
    static void BE16(Stream s, int v) { s.WriteByte((byte)(v >> 8)); s.WriteByte((byte)v); }
}

// A small animated-GIF writer for the README demo. One 256-colour palette for the whole
// animation (median cut over samples of every frame), a light ordered dither (stable from frame
// to frame, so gradients don't shimmer), and each frame stores only the rectangle that changed,
// with unchanged pixels transparent, which keeps the file small.
static class GifWriter
{
    public static void Write(string path, List<Bitmap> frames, int delayCs)
    {
        var pixels = new List<int[]>();
        foreach (var f in frames) pixels.Add(ReadPixels(f));
        Write(path, pixels, frames[0].Width, frames[0].Height, delayCs);
    }

    public static void Write(string path, List<int[]> pixels, int w, int h, int delayCs)
    {
        var palette = BuildPalette(pixels, 255);   // index 255 = transparent
        var cache = new Dictionary<int, byte>();
        int[] bayer = { 0, 8, 2, 10, 12, 4, 14, 6, 3, 11, 1, 9, 15, 7, 13, 5 };

        using (var fs = File.Create(path))
        using (var bw = new BinaryWriter(fs))
        {
            bw.Write(Encoding.ASCII.GetBytes("GIF89a"));
            bw.Write((short)w); bw.Write((short)h);
            bw.Write((byte)0xF7); bw.Write((byte)0); bw.Write((byte)0);
            for (int i = 0; i < 256; i++)
            {
                int c = i < palette.Count ? palette[i] : 0;
                bw.Write((byte)(c >> 16)); bw.Write((byte)(c >> 8)); bw.Write((byte)c);
            }
            bw.Write(new byte[] { 0x21, 0xFF, 0x0B }); bw.Write(Encoding.ASCII.GetBytes("NETSCAPE2.0"));
            bw.Write(new byte[] { 0x03, 0x01, 0x00, 0x00, 0x00 });   // loop forever

            byte[] prev = null;
            for (int fi = 0; fi < pixels.Count; fi++)
            {
                var px = pixels[fi];
                var idx = new byte[w * h];
                for (int y = 0; y < h; y++)
                    for (int x = 0; x < w; x++)
                    {
                        int c = px[y * w + x];
                        int d = bayer[(y & 3) * 4 + (x & 3)] - 8;   // -8..7, small
                        int r = Clamp(((c >> 16) & 255) + d), g = Clamp(((c >> 8) & 255) + d), b = Clamp((c & 255) + d);
                        int key = ((r >> 2) << 12) | ((g >> 2) << 6) | (b >> 2);
                        byte pi;
                        if (!cache.TryGetValue(key, out pi)) { pi = Nearest(palette, r, g, b); cache[key] = pi; }
                        idx[y * w + x] = pi;
                    }
                // only the changed rectangle; unchanged pixels inside it are transparent
                int x0 = 0, y0 = 0, x1 = w - 1, y1 = h - 1;
                if (prev != null)
                {
                    x0 = w; y0 = h; x1 = -1; y1 = -1;
                    for (int y = 0; y < h; y++)
                        for (int x = 0; x < w; x++)
                            if (idx[y * w + x] != prev[y * w + x]) { if (x < x0) x0 = x; if (x > x1) x1 = x; if (y < y0) y0 = y; if (y > y1) y1 = y; }
                    if (x1 < 0) { x0 = y0 = 0; x1 = y1 = 0; }
                }
                int rw = x1 - x0 + 1, rh = y1 - y0 + 1;
                var sub = new byte[rw * rh];
                for (int y = 0; y < rh; y++)
                    for (int x = 0; x < rw; x++)
                    {
                        int i = (y0 + y) * w + x0 + x;
                        sub[y * rw + x] = (prev != null && idx[i] == prev[i]) ? (byte)255 : idx[i];
                    }
                bw.Write(new byte[] { 0x21, 0xF9, 0x04, (byte)((1 << 2) | 1) });   // keep previous frame, transparency on
                bw.Write((short)delayCs); bw.Write((byte)255); bw.Write((byte)0);
                bw.Write((byte)0x2C); bw.Write((short)x0); bw.Write((short)y0); bw.Write((short)rw); bw.Write((short)rh); bw.Write((byte)0);
                Lzw(bw, sub);
                prev = idx;
            }
            bw.Write((byte)0x3B);
        }
    }

    static int Clamp(int v) { return v < 0 ? 0 : v > 255 ? 255 : v; }

    public static int[] ReadPixels(Bitmap b)
    {
        var r = new Rectangle(0, 0, b.Width, b.Height);
        var data = b.LockBits(r, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        var px = new int[b.Width * b.Height];
        Marshal.Copy(data.Scan0, px, 0, px.Length);
        b.UnlockBits(data);
        for (int i = 0; i < px.Length; i++) px[i] &= 0xFFFFFF;
        return px;
    }

    static byte Nearest(List<int> pal, int r, int g, int b)
    {
        int best = 0, bestD = int.MaxValue;
        for (int i = 0; i < pal.Count; i++)
        {
            int c = pal[i], dr = ((c >> 16) & 255) - r, dg = ((c >> 8) & 255) - g, db = (c & 255) - b;
            int d = dr * dr * 3 + dg * dg * 4 + db * db * 2;
            if (d < bestD) { bestD = d; best = i; }
        }
        return (byte)best;
    }

    // median cut over a sample of every frame's pixels
    static List<int> BuildPalette(List<int[]> frames, int count)
    {
        var sample = new List<int>();
        foreach (var px in frames)
            for (int i = 0; i < px.Length; i += 23) sample.Add(px[i]);
        var boxes = new List<List<int>> { sample };
        while (boxes.Count < count)
        {
            int bi = -1, bestRange = 0, bestCh = 0;
            for (int i = 0; i < boxes.Count; i++)
            {
                if (boxes[i].Count < 2) continue;
                for (int ch = 0; ch < 3; ch++)
                {
                    int lo = 255, hi = 0, sh = 16 - ch * 8;
                    foreach (var c in boxes[i]) { int v = (c >> sh) & 255; if (v < lo) lo = v; if (v > hi) hi = v; }
                    int range = (hi - lo) * (ch == 1 ? 4 : 3) * (int)Math.Sqrt(boxes[i].Count);
                    if (range > bestRange) { bestRange = range; bi = i; bestCh = ch; }
                }
            }
            if (bi < 0) break;
            var box = boxes[bi];
            int s = 16 - bestCh * 8;
            box.Sort((a, b) => ((a >> s) & 255).CompareTo((b >> s) & 255));
            int mid = box.Count / 2;
            boxes[bi] = box.GetRange(0, mid);
            boxes.Add(box.GetRange(mid, box.Count - mid));
        }
        var pal = new List<int>();
        foreach (var box in boxes)
        {
            long r = 0, g = 0, b = 0;
            foreach (var c in box) { r += (c >> 16) & 255; g += (c >> 8) & 255; b += c & 255; }
            int n = Math.Max(1, box.Count);
            pal.Add((int)(r / n) << 16 | (int)(g / n) << 8 | (int)(b / n));
        }
        return pal;
    }

    // GIF's LZW, 8-bit codes, variable code size up to 12 bits
    static void Lzw(BinaryWriter bw, byte[] data)
    {
        const int minSize = 8, clear = 256, eoi = 257;
        bw.Write((byte)minSize);
        var outBytes = new List<byte>();
        // (the same code-size rule as the classic GIF encoders: widen after a code is written once
        // the next free code no longer fits; reset to 9 bits right after a clear code)
        int bitBuf = 0, bitCount = 0, nBits = minSize + 1, maxCode = (1 << nBits) - 1, freeEnt = eoi + 1;
        bool clearFlag = false;
        var dict = new Dictionary<int, int>();
        Action<int> emit = code =>
        {
            bitBuf |= code << bitCount; bitCount += nBits;
            while (bitCount >= 8) { outBytes.Add((byte)(bitBuf & 255)); bitBuf >>= 8; bitCount -= 8; }
            if (clearFlag) { nBits = minSize + 1; maxCode = (1 << nBits) - 1; clearFlag = false; }
            else if (freeEnt > maxCode) { nBits++; maxCode = nBits == 12 ? 4096 : (1 << nBits) - 1; }
        };
        emit(clear);
        int ent = data.Length > 0 ? data[0] : 0;
        for (int i = 1; i < data.Length; i++)
        {
            int k = data[i], key = (ent << 8) | k, code;
            if (dict.TryGetValue(key, out code)) { ent = code; continue; }
            emit(ent);
            if (freeEnt < 4096) dict[key] = freeEnt++;
            else { dict.Clear(); freeEnt = eoi + 1; clearFlag = true; emit(clear); }
            ent = k;
        }
        emit(ent);
        emit(eoi);
        if (bitCount > 0) outBytes.Add((byte)(bitBuf & 255));
        for (int i = 0; i < outBytes.Count; i += 255)
        {
            int n = Math.Min(255, outBytes.Count - i);
            bw.Write((byte)n);
            for (int j = 0; j < n; j++) bw.Write(outBytes[i + j]);
        }
        bw.Write((byte)0);
    }
}

// Draws the README pictures ("Prysm Folder.exe" --promo <folder>): the app at 2x over a generated
// wallpaper, with the see-through glass recreated in the image (blur, Prism tint, sheen, rim),
// so no real screen needs capturing. Also makes the app icon.
static class Promo
{
    static string tmp;

    public static void Run(string outDir)
    {
        Directory.CreateDirectory(outDir);
        tmp = Path.Combine(Path.GetTempPath(), "PrysmPromo");
        Directory.CreateDirectory(tmp);
        FolderForm.S = 2f; Theme.FontScale = 2f; Native.Jumbo = true; Theme.Grid3 = true; Theme.Sharp = false;
        Decor.Disabled = true;   // the pictures are drawn without the glass layer: controls paint their own shapes

        string fav = MakeFolder("Favourites", new[] {
            @"C:\Windows\explorer.exe|File Explorer",
            @"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe|Edge",
            @"C:\Windows\System32\notepad.exe|Notepad",
            @"C:\Windows\System32\calc.exe|Calculator",
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads") + "|Downloads",
            @"C:\Windows\System32\Taskmgr.exe|Task Manager",
            "https://github.com|github.com" });
        string tools = MakeFolder("Tools", new[] {
            @"C:\Windows\System32\cmd.exe|Command Prompt", @"C:\Windows\System32\control.exe|Control Panel",
            @"C:\Windows\regedit.exe|Registry Editor", @"C:\Windows\System32\mmc.exe|Console" });
        string work = MakeFolder("Work", new[] {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)) + "|Documents",
            @"C:\Windows\System32\notepad.exe|Notepad", @"C:\Windows\System32\SnippingTool.exe|Snipping Tool" });

        Hero(Path.Combine(outDir, "hero.png"), fav, tools, work);
        Looks(Path.Combine(outDir, "looks-dark.png"), fav, false);
        Looks(Path.Combine(outDir, "looks-light.png"), fav, true);
        Menus(Path.Combine(outDir, "menus.png"), fav);
        Icons(Path.Combine(outDir, "desktop-icons.png"), fav);
        IconGrid(Path.Combine(outDir, "icon-grid.png"), fav);
        Social(Path.Combine(outDir, "social-preview.png"), fav, tools, work);
        Corners(Path.Combine(outDir, "corners.png"), fav);
        using (var icon = AppIcon(256))
        {
            icon.Save(Path.Combine(outDir, "icon.png"), ImageFormat.Png);
            FolderForm.WriteIco(Path.Combine(outDir, "PrysmFolder.ico"), AppIcon(256));
        }
    }

    static string MakeFolder(string name, string[] entries)
    {
        var sb = new StringBuilder("title=" + name + "\r\n");
        foreach (var e in entries)
        {
            string p = e.Substring(0, e.LastIndexOf('|'));
            if (Native.IsUrl(p) || File.Exists(p) || Directory.Exists(p)) sb.Append("item=" + e + "\r\n");
        }
        string f = Path.Combine(tmp, name + Installer.Ext);
        FolderForm.WriteFolderFile(f, FolderForm.RenderPreview(new List<Item>(), 256), sb.ToString());
        return f;
    }

    static List<Item> ItemsOf(string folderFile)
    {
        var list = new List<Item>();
        using (var f = new FolderForm("promo-items", folderFile)) list.AddRange(f.PromoItems);
        return list;
    }

    // ---- scenery ----
    static Bitmap Wallpaper(int w, int h, bool light)
    {
        var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var r = new Rectangle(0, 0, w, h);
            using (var b = new LinearGradientBrush(r, light ? Color.FromArgb(0xe6, 0xea, 0xf6) : Color.FromArgb(0x08, 0x08, 0x16),
                                                      light ? Color.FromArgb(0xf7, 0xec, 0xf4) : Color.FromArgb(0x1a, 0x0c, 0x2e), 35f))
                g.FillRectangle(b, r);
            Blob(g, w * .80f, h * .18f, w * .42f, light ? Color.FromArgb(150, 0xf2, 0x9b, 0xcf) : Color.FromArgb(185, 0xd0, 0x4f, 0xa8));
            Blob(g, w * .12f, h * .92f, w * .48f, light ? Color.FromArgb(150, 0x8c, 0xc4, 0xf5) : Color.FromArgb(165, 0x2f, 0x8f, 0xe0));
            Blob(g, w * .50f, h * .55f, w * .30f, light ? Color.FromArgb(110, 0xb9, 0xa7, 0xf7) : Color.FromArgb(120, 0x6a, 0x3d, 0xf0));
            Blob(g, w * .95f, h * .95f, w * .30f, light ? Color.FromArgb(90, 0xfa, 0xd2, 0x9a) : Color.FromArgb(90, 0xf0, 0x8a, 0x3d));
        }
        return bmp;
    }

    static void Blob(Graphics g, float x, float y, float r, Color c)
    {
        using (var path = new GraphicsPath())
        {
            path.AddEllipse(x - r, y - r, 2 * r, 2 * r);
            using (var b = new PathGradientBrush(path) { CenterColor = c, SurroundColors = new[] { Color.FromArgb(0, c) } })
                g.FillPath(b, path);
        }
    }

    static Bitmap Blur(Bitmap src, Rectangle r)
    {
        var crop = new Bitmap(r.Width, r.Height);
        using (var g = Graphics.FromImage(crop)) g.DrawImage(src, new Rectangle(0, 0, r.Width, r.Height), r, GraphicsUnit.Pixel);
        for (int pass = 0; pass < 3; pass++)
        {
            int sw = Math.Max(1, r.Width / 14), sh = Math.Max(1, r.Height / 14);
            using (var small = new Bitmap(sw, sh))
            {
                using (var g = Graphics.FromImage(small)) { g.InterpolationMode = InterpolationMode.HighQualityBilinear; g.DrawImage(crop, 0, 0, sw, sh); }
                using (var g = Graphics.FromImage(crop)) { g.InterpolationMode = InterpolationMode.HighQualityBicubic; g.PixelOffsetMode = PixelOffsetMode.Half; g.DrawImage(small, 0, 0, r.Width, r.Height); }
            }
        }
        return crop;
    }

    static void Shadow(Graphics g, Rectangle r, float radius)
    {
        for (int i = 16; i >= 1; i--)
            using (var p = FolderForm.Rounded(new RectangleF(r.X - i, r.Y - i + 10, r.Width + 2 * i, r.Height + 2 * i), radius + i))
            using (var b = new SolidBrush(Color.FromArgb(6, 0, 0, 0))) g.FillPath(b, p);
    }

    // A card (folder, menu, settings) as it looks on the Desktop: Prism glass or the solid panel,
    // with the window's own content on top.
    static void Card(Graphics g, Bitmap wall, Rectangle r, float radius, Bitmap content)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        Shadow(g, r, radius);
        using (var path = FolderForm.Rounded(r, radius))
        {
            if (Theme.Glass)
            {
                using (var blurred = Blur(wall, r))
                {
                    g.SetClip(path);
                    g.DrawImage(blurred, r.Location);
                    Color panel = Theme.Light ? Color.FromArgb(0xf3, 0xf3, 0xf5) : Color.FromArgb(0x14, 0x14, 0x16);
                    using (var b = new SolidBrush(Color.FromArgb(Theme.Light ? 115 : 87, panel))) g.FillRectangle(b, r);
                    var blend = new ColorBlend
                    {
                        Positions = new[] { 0f, .6f, 1f },
                        Colors = Theme.Light ? new[] { Color.FromArgb(107, 255, 255, 255), Color.FromArgb(66, 255, 255, 255), Color.FromArgb(77, 255, 255, 255) }
                                             : new[] { Color.FromArgb(18, 255, 255, 255), Color.FromArgb(9, 255, 255, 255), Color.FromArgb(13, 255, 255, 255) }
                    };
                    using (var b = new LinearGradientBrush(r, Color.White, Color.White, 90f) { InterpolationColors = blend }) g.FillRectangle(b, r);
                    g.ResetClip();
                }
                using (var pen = new Pen(Color.FromArgb(Theme.Light ? 153 : 38, 255, 255, 255), 2f)) g.DrawPath(pen, path);
                content.MakeTransparent(Theme.Key);
                g.SetClip(path);
                g.DrawImage(content, r.Location);
                g.ResetClip();
            }
            else
            {
                g.SetClip(path);
                g.DrawImage(content, r.Location);
                g.ResetClip();
                using (var pen = new Pen(Theme.Rim, 2f)) g.DrawPath(pen, path);
            }
        }
    }

    static Bitmap Capture(Control c)
    {
        var bmp = new Bitmap(c.Width, c.Height);
        c.DrawToBitmap(bmp, new Rectangle(0, 0, c.Width, c.Height));
        return bmp;
    }

    static FolderForm OpenOffscreen(string file)
    {
        var f = new FolderForm("promo", file) { OpenAt = new Point(-30000, -30000) };
        f.Show();
        Application.DoEvents();
        return f;
    }

    static void DesktopIcon(Graphics g, List<Item> items, string label, int x, int y)
    {
        using (var icon = FolderForm.RenderPreview(items, 256)) { g.InterpolationMode = InterpolationMode.HighQualityBicubic; g.DrawImage(icon, x, y, 112, 112); }
        Caption(g, label, new Rectangle(x - 40, y + 118, 192, 40), 9f, false, true);
    }

    static void Caption(Graphics g, string text, Rectangle r, float pt, bool bold, bool shadow, Color? color = null)
    {
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
        using (var f = Theme.UiFont(pt, bold ? FontStyle.Bold : FontStyle.Regular, bold))
        using (var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Near })
        {
            if (shadow) using (var b = new SolidBrush(Color.FromArgb(150, 0, 0, 0))) g.DrawString(text, f, b, new Rectangle(r.X + 2, r.Y + 2, r.Width, r.Height), sf);
            using (var b = new SolidBrush(color ?? Color.White)) g.DrawString(text, f, b, r, sf);
        }
    }

    // ---- pictures ----
    static void Hero(string outPng, string fav, string tools, string work)
    {
        Theme.Apply(false, true);
        using (var wall = Wallpaper(1800, 1000, false))
        using (var g = Graphics.FromImage(wall))
        {
            int y = 70;
            foreach (var f in new[] { fav, tools, work })
            {
                DesktopIcon(g, ItemsOf(f), Path.GetFileNameWithoutExtension(f), 70, y);
                y += 200;
            }
            var form = OpenOffscreen(fav);
            using (var content = Capture(form))
            {
                var r = new Rectangle(330, 110, content.Width, content.Height);
                using (var clean = (Bitmap)wall.Clone()) Card(g, clean, r, Theme.Radius(24) * 2, content);
            }
            form.Dispose();
            wall.Save(outPng, ImageFormat.Png);
        }
    }

    static void Looks(string outPng, string fav, bool light)
    {
        using (var wall = Wallpaper(1900, 860, light))
        using (var g = Graphics.FromImage(wall))
        {
            int x = 70;
            foreach (bool glass in new[] { true, false })
            {
                Theme.Apply(light, glass);
                var form = OpenOffscreen(fav);
                using (var content = Capture(form))
                {
                    var r = new Rectangle(x, 150, content.Width, content.Height);
                    using (var clean = (Bitmap)wall.Clone()) Card(g, clean, r, Theme.Radius(24) * 2, content);
                    Caption(g, glass ? "See-through" : "Solid", new Rectangle(x, 60, content.Width, 60), 17f, true, !light,
                            light ? Color.FromArgb(0x17, 0x17, 0x1b) : Color.White);
                    x += content.Width + 120;
                }
                form.Dispose();
            }
            wall.Save(outPng, ImageFormat.Png);
        }
    }

    static void Menus(string outPng, string fav)
    {
        Theme.Apply(false, true);
        using (var wall = Wallpaper(1800, 1000, false))
        using (var g = Graphics.FromImage(wall))
        {
            var form = OpenOffscreen(fav);
            var fr = new Rectangle(90, 110, form.Width, form.Height);
            using (var content = Capture(form))
            using (var clean = (Bitmap)wall.Clone()) Card(g, clean, fr, Theme.Radius(24) * 2, content);

            // right-click on an app
            using (var m = new PrismMenu(form))
            {
                m.Add("Open", '\uE8A7', null);
                m.Add("Run as administrator", '\uE7EF', null);
                m.Add("Open file location", '\uE838', null);
                m.AddSeparator();
                m.Add("Rename", '\uE8AC', null);
                m.Add("Move left", '\uE76B', null);
                m.Add("Move right", '\uE76C', null);
                m.AddSeparator();
                m.Add("Remove from folder", '\uE74D', null, true);
                m.Measure();
                using (var mc = Capture(m))
                using (var clean = (Bitmap)wall.Clone())
                    Card(g, clean, new Rectangle(fr.X + 330, fr.Y + 300, mc.Width, mc.Height), Theme.Radius(12) * 2, mc);
            }
            // the settings card under the gear
            using (var card = new SettingsCard(form, x => { }))
            using (var cc = Capture(card))
            using (var clean = (Bitmap)wall.Clone())
                Card(g, clean, new Rectangle(fr.Right + 80, fr.Y + 40, cc.Width, cc.Height), Theme.Radius(16) * 2, cc);
            form.Dispose();
            wall.Save(outPng, ImageFormat.Png);
        }
    }

    static void Icons(string outPng, string fav)
    {
        var items = ItemsOf(fav);
        using (var wall = Wallpaper(1500, 420, false))
        using (var g = Graphics.FromImage(wall))
        {
            int x = 90;
            foreach (bool light in new[] { false, true })
                foreach (bool glass in new[] { true, false })
                {
                    Theme.Apply(light, glass);
                    using (var icon = FolderForm.RenderPreview(items, 256)) { g.InterpolationMode = InterpolationMode.HighQualityBicubic; g.DrawImage(icon, x, 70, 190, 190); }
                    Caption(g, (light ? "Light" : "Dark") + ", " + (glass ? "see-through" : "solid"), new Rectangle(x - 60, 280, 310, 60), 11f, false, true);
                    x += 350;
                }
            wall.Save(outPng, ImageFormat.Png);
        }
    }

    // ================= the demo GIF ("Prysm Folder.exe" --demo out.gif) =================
    // An 8-second session drawn frame by frame with the app's own rendering and its real
    // animation timings: point at a folder (it springs open), hover the apps, right-click for the
    // menu, open settings and switch to Solid, then click away (it closes).
    public static void Demo(string outGif)
    {
        tmp = Path.Combine(Path.GetTempPath(), "PrysmPromo");
        Directory.CreateDirectory(tmp);
        FolderForm.S = 1.25f; Theme.FontScale = 1.25f; Native.Jumbo = true; Theme.Grid3 = true; Theme.Sharp = false;
        Decor.Disabled = true;
        bool apng = outGif.EndsWith(".png", StringComparison.OrdinalIgnoreCase);   // .png: animated PNG at 60 fps
        const int W = 1000, H = 600;
        int FPS = apng ? 60 : 20;
        const float T = 8.2f;

        string fav = MakeFolder("Favourites", new[] {
            @"C:\Windows\explorer.exe|File Explorer", @"C:\Windows\System32\notepad.exe|Notepad",
            @"C:\Windows\System32\calc.exe|Calculator",
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads") + "|Downloads",
            @"C:\Windows\System32\Taskmgr.exe|Task Manager", "https://github.com|github.com" });
        string tools = MakeFolder("Tools", new[] {
            @"C:\Windows\System32\cmd.exe|Command Prompt", @"C:\Windows\System32\control.exe|Control Panel",
            @"C:\Windows\regedit.exe|Registry Editor", @"C:\Windows\System32\mmc.exe|Console" });
        string work = MakeFolder("Work", new[] {
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments) + "|Documents",
            @"C:\Windows\System32\notepad.exe|Notepad", @"C:\Windows\System32\SnippingTool.exe|Snipping Tool" });

        // ---- the desktop ----
        Theme.Apply(false, true);
        var desk = Wallpaper(W, H, false);
        var iconSpots = new List<Point>();
        using (var g = Graphics.FromImage(desk))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            int y = 34;
            foreach (var f in new[] { fav, tools, work })
            {
                using (var ic = FolderForm.RenderPreview(ItemsOf(f), 256)) { g.InterpolationMode = InterpolationMode.HighQualityBicubic; g.DrawImage(ic, 34, y, 84, 84); }
                Caption(g, Path.GetFileNameWithoutExtension(f), new Rectangle(4, y + 88, 144, 30), 6.2f, false, true);
                iconSpots.Add(new Point(34 + 42, y + 42));
                y += 150;
            }
        }

        // ---- every state of the folder, menu and settings, captured once ----
        var folderAt = new Point(170, 40);
        var glassForm = OpenOffscreen(fav);
        var fRect = new Rectangle(folderAt, glassForm.Size);
        Func<int, Bitmap> pose = hover => { glassForm.PromoHover(hover); return Capture(glassForm); };
        var glassStates = new Dictionary<int, Bitmap> { { -1, pose(-1) }, { 1, pose(1) }, { -3, pose(-3) } };
        Rectangle tileNotepad = Off(glassForm.PromoTileRect(1), folderAt), tileExplorer = Off(glassForm.PromoTileRect(0), folderAt);
        Rectangle gear = Off(glassForm.PromoGearRect, folderAt), close = Off(glassForm.PromoCloseRect, folderAt);

        var menu = new PrismMenu(glassForm);
        menu.Add("Open", '\uE8A7', null); menu.Add("Run as administrator", '\uE7EF', null); menu.Add("Open file location", '\uE838', null);
        menu.AddSeparator(); menu.Add("Rename", '\uE8AC', null); menu.Add("Move right", '\uE76C', null);
        menu.AddSeparator(); menu.Add("Remove from folder", '\uE74D', null, true);
        menu.Measure();
        var menuAt = new Point(tileNotepad.X + tileNotepad.Width / 2 + 6, tileNotepad.Y + tileNotepad.Height / 2 + 6);
        var menuStates = new Dictionary<int, Bitmap>();
        foreach (int h in new[] { -1, 0, 1, 2, 4 }) { menu.PromoHot = h; menuStates[h] = Capture(menu); }
        Func<int, Rectangle> menuRow = i => Off(menu.PromoRow(i), menuAt);

        var card = new SettingsCard(glassForm, x => { });
        var cardAt = new Point(close.Right - card.Width, gear.Bottom + (int)(8 * FolderForm.S));
        var cardStates = new Dictionary<int, Bitmap>();
        foreach (int h in new[] { -1, 2 }) { card.PromoHot = h; cardStates[h] = Capture(card); }
        var solidPill = Off(card.PromoSolidRect, cardAt);

        // the glass layers (blur of the desktop behind them), made once per state
        var layers = new Dictionary<string, Bitmap>();
        Func<string, Rectangle, float, Bitmap, Bitmap> layer = (key, r, rad, content) =>
        {
            Bitmap b;
            if (!layers.TryGetValue(key, out b)) layers[key] = b = CardLayer(desk, r, rad, content);
            return b;
        };
        float folderRad = 24 * FolderForm.S;
        foreach (var kv in glassStates) layer("g" + kv.Key, fRect, folderRad, kv.Value);
        foreach (var kv in menuStates) layer("m" + kv.Key, new Rectangle(menuAt, menu.Size), 12 * FolderForm.S, kv.Value);
        foreach (var kv in cardStates) layer("c" + kv.Key, new Rectangle(cardAt, card.Size), 16 * FolderForm.S, kv.Value);
        menu.Dispose(); card.Dispose(); glassForm.Dispose();

        Theme.Apply(false, false);   // the Solid look
        var solidForm = OpenOffscreen(fav);
        solidForm.PromoHover(-1); var solidPlain = Capture(solidForm);
        solidForm.PromoHover(0); var solidHover = Capture(solidForm);
        solidForm.Dispose();
        layer("s-1", fRect, folderRad, solidPlain);
        layer("s0", fRect, folderRad, solidHover);
        Theme.Apply(false, true);

        // ---- the script ----
        var cursorPath = new List<KeyValuePair<float, PointF>>();
        Action<float, PointF> at = (t, p) => cursorPath.Add(new KeyValuePair<float, PointF>(t, p));
        Func<Rectangle, PointF> mid = r => new PointF(r.X + r.Width / 2f, r.Y + r.Height / 2f);
        var favIcon = iconSpots[0];
        at(0.0f, new PointF(840, 480)); at(0.85f, favIcon); at(1.7f, favIcon);
        at(2.4f, new PointF(mid(tileNotepad).X, mid(tileNotepad).Y - 10)); at(2.65f, new PointF(mid(tileNotepad).X, mid(tileNotepad).Y - 10));
        PointF m0 = menuAt;
        at(2.95f, new PointF(m0.X + 60, mid(menuRow(0)).Y)); at(3.2f, new PointF(m0.X + 70, mid(menuRow(1)).Y));
        at(3.45f, new PointF(m0.X + 70, mid(menuRow(2)).Y)); at(3.7f, new PointF(m0.X + 70, mid(menuRow(4)).Y));
        at(4.1f, new PointF(fRect.X + 90, fRect.Bottom - 30)); at(4.25f, new PointF(fRect.X + 90, fRect.Bottom - 30));
        at(4.8f, mid(gear)); at(5.0f, mid(gear));
        at(5.55f, mid(solidPill)); at(5.75f, mid(solidPill));
        at(6.5f, mid(tileExplorer)); at(6.8f, mid(tileExplorer));
        at(7.35f, new PointF(880, 470)); at(T, new PointF(840, 480));

        float openAt = 0.95f, menuOpen = 2.7f, menuClose = 4.25f, cardOpen = 5.0f, solidSwitch = 5.8f, closeAt = 7.4f;
        var clicks = new[] { new KeyValuePair<float, bool>(2.7f, true), new KeyValuePair<float, bool>(4.25f, false),
                             new KeyValuePair<float, bool>(5.0f, false), new KeyValuePair<float, bool>(5.8f, false), new KeyValuePair<float, bool>(7.4f, false) };
        var captions = new[] {
            Tuple.Create(0.25f, 2.55f, "Point at a folder to open it"),
            Tuple.Create(2.6f, 4.3f, "Right-click an app for options"),
            Tuple.Create(4.4f, 6.9f, "Settings: See-through or Solid"),
            Tuple.Create(7.0f, T, "Click anywhere else to close") };

        var frames = new List<int[]>();
        int n = (int)(T * FPS);
        ApngWriter png = apng ? new ApngWriter(outGif, W, H, n, 1, FPS) : null;
        for (int fi = 0; fi < n; fi++)
        {
            float t = fi / (float)FPS;
            using (var frame = (Bitmap)desk.Clone())
            using (var g = Graphics.FromImage(frame))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.InterpolationMode = InterpolationMode.HighQualityBilinear;
                var cur = CursorAt(cursorPath, t);

                // the folder: glass until the switch, then Solid; springs open, shrinks away at the end
                bool solid = t >= solidSwitch + 0.12f;
                string key;
                if (!solid)
                {
                    int hover = -1;
                    if (t >= 2.25f && t < 2.75f) hover = 1;
                    else if (t >= 4.7f && t < 5.05f) hover = -3;
                    key = "g" + hover;
                }
                else key = (t >= 6.4f && t < 7.4f) ? "s0" : "s-1";
                float s = 1, a = 1;
                if (t < openAt) a = 0;
                else if (t < solidSwitch)
                {
                    Spring(t - openAt, 0.1f, out s, out a);
                    if (t >= solidSwitch - 0.0001f) { }
                }
                if (t >= solidSwitch && t < solidSwitch + 0.12f) { float e = (t - solidSwitch) / 0.12f; s = 1 - 0.07f * e; a = 1 - e * e; }
                else if (t >= solidSwitch + 0.12f && t < closeAt) Spring(t - solidSwitch - 0.12f, 0.1f, out s, out a);
                if (t >= closeAt) { float e = Math.Min(1, (t - closeAt) / 0.12f); s = 1 - 0.07f * e; a = 1 - e * e; }
                if (a > 0.01f) DrawLayer(g, layers[key], fRect, s, a);

                // the right-click menu
                if (t >= menuOpen && t < menuClose + 0.09f)
                {
                    int hot = -1;
                    float my = cur.Y;
                    foreach (int i in new[] { 0, 1, 2, 4 }) if (cur.X > menuAt.X && menuRow(i).Contains((int)cur.X, (int)my)) hot = i;
                    float ms, ma;
                    if (t < menuClose) Spring(t - menuOpen, 0.06f, out ms, out ma);
                    else { float e = (t - menuClose) / 0.09f; ms = 1 - 0.05f * e; ma = 1 - e * e; }
                    DrawLayer(g, layers["m" + hot], new Rectangle(menuAt, menu.Size), ms, ma, true);
                }
                // the settings card
                if (t >= cardOpen && t < solidSwitch + 0.1f)
                {
                    int hot = solidPill.Contains((int)cur.X, (int)cur.Y) ? 2 : -1;
                    float cs, ca;
                    if (t < solidSwitch) Spring(t - cardOpen, 0.06f, out cs, out ca);
                    else { float e = (t - solidSwitch) / 0.1f; cs = 1 - 0.05f * e; ca = 1 - e * e; }
                    DrawLayer(g, layers["c" + hot], new Rectangle(cardAt, card.Size), cs, ca, true);
                }

                // clicks: a little ripple (blue for a right-click)
                foreach (var c in clicks)
                {
                    float dt = t - c.Key;
                    if (dt < 0 || dt > 0.35f) continue;
                    var cp = CursorAt(cursorPath, c.Key);
                    float rr = 6 + dt / 0.35f * 20;
                    using (var p = new Pen(Color.FromArgb((int)(170 * (1 - dt / 0.35f)), c.Value ? Theme.Accent : Color.White), 2.5f))
                        g.DrawEllipse(p, cp.X - rr, cp.Y - rr, 2 * rr, 2 * rr);
                }

                // caption
                foreach (var cap in captions)
                    if (t >= cap.Item1 && t < cap.Item2)
                    {
                        float fade = Math.Min(1, Math.Min((t - cap.Item1) / 0.2f, (cap.Item2 - t) / 0.2f));
                        CaptionPill(g, cap.Item3, W, H, fade);
                    }

                DrawCursor(g, cur);
                if (png != null) png.AddFrame(GifWriter.ReadPixels(frame));
                else frames.Add(GifWriter.ReadPixels(frame));
            }
        }
        if (png != null) png.Dispose();
        else GifWriter.Write(outGif, frames, W, H, 100 / FPS);
    }

    static Rectangle Off(Rectangle r, Point by) { r.Offset(by); return r; }

    // the card as it sits on the desktop (glass or solid, with its shadow), on a transparent layer
    static Bitmap CardLayer(Bitmap wall, Rectangle r, float radius, Bitmap content)
    {
        var lay = new Bitmap(wall.Width, wall.Height, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(lay)) Card(g, wall, r, radius, (Bitmap)content.Clone());
        return lay;
    }

    // draws a layer scaled around the card's centre, faded
    static void DrawLayer(Graphics g, Bitmap lay, Rectangle card, float scale, float alpha, bool clip = false)
    {
        // settled: draw it exactly, so the end of an animation doesn't "snap" from resampled to crisp
        if (Math.Abs(scale - 1) < 0.0015f) scale = 1;
        float cx = card.X + card.Width / 2f, cy = card.Y + card.Height / 2f;
        var state = g.Save();
        // sub-pixel placement while scaling (no rounding to whole pixels, which made it wobble)
        g.InterpolationMode = scale == 1 ? InterpolationMode.NearestNeighbor : InterpolationMode.HighQualityBilinear;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.TranslateTransform(cx, cy);
        g.ScaleTransform(scale, scale);
        g.TranslateTransform(-cx, -cy);
        using (var ia = new ImageAttributes())
        {
            var cm = new ColorMatrix { Matrix33 = Math.Max(0, Math.Min(1, alpha)) };
            ia.SetColorMatrix(cm);
            var src = Rectangle.Inflate(card, 40, 40);
            src.Intersect(new Rectangle(0, 0, lay.Width, lay.Height));
            g.DrawImage(lay, src, src.X, src.Y, src.Width, src.Height, GraphicsUnit.Pixel, ia);
        }
        g.Restore(state);
    }

    // Prism's "settle": scale springs up to 1 with a small overshoot, fading in over 140 ms
    static void Spring(float t, float amount, out float scale, out float alpha)
    {
        scale = 1 - amount * (float)(Math.Exp(-t / 0.09) * Math.Cos(2 * Math.PI * t / 0.32));
        float f = Math.Min(1, t / 0.14f);
        alpha = 1 - (1 - f) * (1 - f) * (1 - f);
    }

    static PointF CursorAt(List<KeyValuePair<float, PointF>> path, float t)
    {
        for (int i = 1; i < path.Count; i++)
        {
            if (t > path[i].Key) continue;
            var a = path[i - 1]; var b = path[i];
            float f = b.Key <= a.Key ? 1 : (t - a.Key) / (b.Key - a.Key);
            f = Math.Max(0, Math.Min(1, f));
            f = f * f * (3 - 2 * f);   // ease in and out
            return new PointF(a.Value.X + (b.Value.X - a.Value.X) * f, a.Value.Y + (b.Value.Y - a.Value.Y) * f);
        }
        return path[path.Count - 1].Value;
    }

    static void DrawCursor(Graphics g, PointF p)
    {
        var pts = new[] { new PointF(0, 0), new PointF(0, 17), new PointF(4, 13), new PointF(7, 20), new PointF(9.4f, 19), new PointF(6.4f, 12.2f), new PointF(11.5f, 12.2f) };
        float k = 1.35f;
        for (int i = 0; i < pts.Length; i++) pts[i] = new PointF(p.X + pts[i].X * k, p.Y + pts[i].Y * k);
        using (var sh = new SolidBrush(Color.FromArgb(70, 0, 0, 0)))
        {
            var spts = (PointF[])pts.Clone();
            for (int i = 0; i < spts.Length; i++) spts[i] = new PointF(spts[i].X + 1.5f, spts[i].Y + 2f);
            g.FillPolygon(sh, spts);
        }
        g.FillPolygon(Brushes.White, pts);
        using (var pen = new Pen(Color.Black, 1.3f) { LineJoin = LineJoin.Round }) g.DrawPolygon(pen, pts);
    }

    static void CaptionPill(Graphics g, string text, int W, int H, float fade)
    {
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
        using (var f = new Font(Theme.UiFont(1f).FontFamily, 17f, FontStyle.Regular, GraphicsUnit.Pixel))
        {
            var sz = g.MeasureString(text, f);
            float pw = sz.Width + 34, ph = 36, x = (W - pw) / 2, y = H - ph - 18;
            using (var path = FolderForm.Rounded(new RectangleF(x, y, pw, ph), ph / 2))
            {
                using (var b = new SolidBrush(Color.FromArgb((int)(175 * fade), 12, 12, 20))) g.FillPath(b, path);
                using (var pen = new Pen(Color.FromArgb((int)(60 * fade), 255, 255, 255), 1f)) g.DrawPath(pen, path);
            }
            using (var b = new SolidBrush(Color.FromArgb((int)(255 * fade), 255, 255, 255)))
                g.DrawString(text, f, b, x + 17, y + (ph - sz.Height) / 2);
        }
    }

    // GitHub's social preview (1280 x 640): what Discord and others show for the repo link.
    // The app icon and name over the Prism wallpaper, the tagline, and the glass folder rising
    // from the bottom edge with a few folder icons beside it.
    static void Social(string outPng, string fav, string tools, string work)
    {
        Theme.Apply(false, true);
        Theme.Sharp = false;
        const int W = 1280, H = 640;
        using (var wall = Wallpaper(W, H, false))
        using (var g = Graphics.FromImage(wall))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;

            // title row: icon + name, centred
            using (var titleFont = new Font(Theme.UiFont(1f, FontStyle.Bold, true).FontFamily, 54f, FontStyle.Bold, GraphicsUnit.Pixel))
            using (var tagFont = new Font(Theme.UiFont(1f).FontFamily, 26f, FontStyle.Regular, GraphicsUnit.Pixel))
            {
                const string name = "Prysm Folder";
                var ns = g.MeasureString(name, titleFont);
                int icon = 76, gap = 22;
                float rowW = icon + gap + ns.Width, x0 = (W - rowW) / 2f, y0 = 46;
                using (var ic = AppIcon(256)) g.DrawImage(ic, x0, y0, icon, icon);
                using (var sh = new SolidBrush(Color.FromArgb(120, 0, 0, 0))) g.DrawString(name, titleFont, sh, x0 + icon + gap + 2, y0 + (icon - ns.Height) / 2f + 3);
                using (var b = new SolidBrush(Color.White)) g.DrawString(name, titleFont, b, x0 + icon + gap, y0 + (icon - ns.Height) / 2f);
                using (var sf = new StringFormat { Alignment = StringAlignment.Center })
                {
                    var tr = new RectangleF(0, y0 + icon + 18, W, 40);
                    using (var sh = new SolidBrush(Color.FromArgb(120, 0, 0, 0))) g.DrawString("Phone-style app folders for your Windows 11 desktop", tagFont, sh, new RectangleF(tr.X + 2, tr.Y + 2, tr.Width, tr.Height), sf);
                    using (var b = new SolidBrush(Color.FromArgb(230, 255, 255, 255))) g.DrawString("Phone-style app folders for your Windows 11 desktop", tagFont, b, tr, sf);
                }
            }

            // the glass folder, rising from the bottom edge (scaled from the 2x render)
            var form = OpenOffscreen(fav);
            using (var content = Capture(form))
            {
                float k = 0.78f;
                int cw = (int)(content.Width * k), ch = (int)(content.Height * k);
                var r = new Rectangle((W - cw) / 2 + 70, 220, cw, ch);
                content.MakeTransparent(Theme.Key);   // before shrinking, so edges blend into the glass, not the key colour
                using (var small = new Bitmap(cw, ch, PixelFormat.Format32bppArgb))
                using (var clean = (Bitmap)wall.Clone())
                {
                    using (var sg = Graphics.FromImage(small)) { sg.InterpolationMode = InterpolationMode.HighQualityBicubic; sg.DrawImage(content, 0, 0, cw, ch); }
                    Card(g, clean, r, Theme.Radius(24) * 2 * k, small);
                }
                // folder icons on the "desktop" to its left
                int y = 250;
                foreach (var f in new[] { fav, tools, work })
                {
                    using (var ico = FolderForm.RenderPreview(ItemsOf(f), 256)) g.DrawImage(ico, r.X - 170, y, 104, 104);
                    Caption(g, Path.GetFileNameWithoutExtension(f), new Rectangle(r.X - 210, y + 108, 184, 36), 8.5f, false, true);
                    y += 150;
                }
            }
            form.Dispose();
            wall.Save(outPng, ImageFormat.Png);
        }
    }

    // The same folder with Rounded and with Sharper corners (window and Desktop icon)
    static void Corners(string outPng, string fav)
    {
        var items = ItemsOf(fav);
        using (var wall = Wallpaper(1900, 860, false))
        using (var g = Graphics.FromImage(wall))
        {
            int x = 70;
            foreach (bool sharp in new[] { false, true })
            {
                Theme.Sharp = sharp;
                Theme.Apply(false, true);
                var form = OpenOffscreen(fav);
                using (var content = Capture(form))
                {
                    var r = new Rectangle(x, 150, content.Width, content.Height);
                    using (var clean = (Bitmap)wall.Clone()) Card(g, clean, r, Theme.Radius(24) * 2, content);
                    Caption(g, sharp ? "Sharper" : "Rounded", new Rectangle(x, 60, content.Width, 60), 17f, true, true);
                    using (var icon = FolderForm.RenderPreview(items, 256)) { g.InterpolationMode = InterpolationMode.HighQualityBicubic; g.DrawImage(icon, x + content.Width - 150, r.Bottom + 20, 112, 112); }
                    x += content.Width + 120;
                }
                form.Dispose();
            }
            Theme.Sharp = false;
            wall.Save(outPng, ImageFormat.Png);
        }
    }

    // The same folder (more than 4 apps) as a 2 x 2 and as a 3 x 3 Desktop icon
    static void IconGrid(string outPng, string fav)
    {
        var items = ItemsOf(fav);
        Theme.Apply(false, true);
        using (var wall = Wallpaper(1100, 420, false))
        using (var g = Graphics.FromImage(wall))
        {
            int x = 200;
            foreach (bool grid3 in new[] { false, true })
            {
                Theme.Grid3 = grid3;
                using (var icon = FolderForm.RenderPreview(items, 256)) { g.InterpolationMode = InterpolationMode.HighQualityBicubic; g.DrawImage(icon, x, 60, 220, 220); }
                Caption(g, grid3 ? "3 × 3" : "2 × 2", new Rectangle(x - 60, 300, 340, 60), 14f, true, true);
                x += 480;
            }
            Theme.Grid3 = true;
            wall.Save(outPng, ImageFormat.Png);
        }
    }

    // The app's own icon: a Prism-style tile with four little app squares.
    public static Bitmap AppIcon(int size)
    {
        var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            float m = size * 0.04f;
            var box = new RectangleF(m, m, size - 2 * m, size - 2 * m);
            using (var p = FolderForm.Rounded(box, size * 0.22f))
            {
                using (var b = new LinearGradientBrush(box, Color.FromArgb(0x3a, 0x2a, 0x6e), Color.FromArgb(0x14, 0x12, 0x2c), 55f)) g.FillPath(b, p);
                g.SetClip(p);
                Blob(g, box.Right - size * 0.12f, box.Top + size * 0.08f, size * 0.62f, Color.FromArgb(150, 0xd0, 0x4f, 0xa8));
                Blob(g, box.Left + size * 0.05f, box.Bottom, size * 0.55f, Color.FromArgb(110, 0x2f, 0x8f, 0xe0));
                g.ResetClip();
                using (var pen = new Pen(Color.FromArgb(70, 255, 255, 255), Math.Max(1f, size / 96f))) g.DrawPath(pen, p);
            }
            var colors = new[] { Color.FromArgb(0x4c, 0xc2, 0xff), Color.FromArgb(0xff, 0x4f, 0x8b), Color.FromArgb(0x8b, 0x6c, 0xff), Color.FromArgb(0x3d, 0xe0, 0xb5) };
            float cell = size * 0.27f, gap = size * 0.07f, start = (size - 2 * cell - gap) / 2;
            for (int i = 0; i < 4; i++)
            {
                var r = new RectangleF(start + (i % 2) * (cell + gap), start + (i / 2) * (cell + gap), cell, cell);
                using (var p = FolderForm.Rounded(r, cell * 0.28f))
                {
                    using (var b = new LinearGradientBrush(r, ControlPaint.Light(colors[i], .35f), colors[i], 90f)) g.FillPath(b, p);
                    using (var pen = new Pen(Color.FromArgb(90, 255, 255, 255), Math.Max(1f, size / 128f))) g.DrawPath(pen, p);
                }
            }
        }
        return bmp;
    }
}

static class Program
{
    [DllImport("user32.dll")] static extern uint GetGuiResources(IntPtr process, uint flags);

    [STAThread]
    static void Main(string[] args)
    {
        try { Native.SetProcessDPIAware(); } catch { }
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        using (var g = Graphics.FromHwnd(IntPtr.Zero)) FolderForm.S = g.DpiX / 96f;
        Theme.Load();

        string arg = args.Length > 0 ? args[0].Trim() : "";
        bool quiet = Array.IndexOf(args, "--quiet") >= 0;
        FolderForm.OpenedByHover = Array.IndexOf(args, "--hover") >= 0;
        if (arg == "--watch") { Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException); HoverWatcher.Run(); return; }
        if (arg == "--probe" && args.Length > 1) { HoverWatcher.Probe(args[1]); return; }
        if (arg == "--opentest" && args.Length > 2)
        {
            // Self-test: how fast a folder opens (cold, then warm) and that the animation completes.
            var sb = new StringBuilder();
            try
            {
                for (int i = 0; i < 3; i++)
                {
                    var sw = Stopwatch.StartNew();
                    var f = new FolderForm("opentest" + i, Path.GetFullPath(args[1])) { OpenAt = new Point(-30000, -30000) };
                    double shown = -1;
                    f.Shown += (s, e) => shown = sw.Elapsed.TotalMilliseconds;
                    f.Show();
                    while (sw.ElapsedMilliseconds < 2000 && (shown < 0 || f.Opacity < 1)) { Application.DoEvents(); Thread.Sleep(1); }
                    sb.AppendLine((i == 0 ? "cold" : "warm") + ": on screen after " + shown.ToString("0") + " ms, animation done after " +
                                  sw.Elapsed.TotalMilliseconds.ToString("0") + " ms, opacity " + f.Opacity + ", glass " + (Theme.Glass ? "on" : "off"));
                    var cw = Stopwatch.StartNew();
                    bool closed = false;
                    f.FormClosed += (s, e) => closed = true;
                    f.Close();
                    while (!closed && cw.ElapsedMilliseconds < 2000) { Application.DoEvents(); Thread.Sleep(1); }
                    sb.AppendLine("      closed after " + cw.Elapsed.TotalMilliseconds.ToString("0") + " ms (animated: " + closed + ")");
                }
            }
            catch (Exception ex) { sb.AppendLine("ERROR " + ex); }
            File.WriteAllText(args[2], sb.ToString());
            return;
        }
        if (arg == "--leaktest" && args.Length > 2)
        {
            // Self-test: opens and closes a folder many times, like the hover helper does all day,
            // and reports whether Windows handles (GDI / USER objects) keep piling up.
            var sb = new StringBuilder();
            try
            {
                Func<string> counts = () =>
                {
                    GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
                    var h = Process.GetCurrentProcess().Handle;
                    return "GDI " + GetGuiResources(h, 0) + ", USER " + GetGuiResources(h, 1);
                };
                Action openClose = () =>
                {
                    var f = new FolderForm("leaktest", Path.GetFullPath(args[1])) { OpenAt = new Point(-30000, -30000) };
                    bool closed = false;
                    f.FormClosed += (s, e) => closed = true;
                    f.Show();
                    var sw = Stopwatch.StartNew();
                    while (sw.ElapsedMilliseconds < 400) { Application.DoEvents(); Thread.Sleep(2); }
                    f.Close();
                    sw.Restart();
                    while (!closed && sw.ElapsedMilliseconds < 1000) { Application.DoEvents(); Thread.Sleep(2); }
                    f.Dispose();
                    Application.DoEvents();
                };
                for (int i = 0; i < 3; i++) openClose();   // warm up caches first
                sb.AppendLine("after 3 opens:  " + counts());
                for (int i = 0; i < 40; i++) openClose();
                sb.AppendLine("after 43 opens: " + counts());
            }
            catch (Exception ex) { sb.AppendLine("ERROR " + ex); }
            File.WriteAllText(args[2], sb.ToString());
            return;
        }
        if (arg == "--demo" && args.Length > 1)
        {
            try { Promo.Demo(args[1]); }
            catch (Exception ex) { File.WriteAllText(args[1] + ".error.txt", ex.ToString()); }
            return;
        }
        if (arg == "--promo" && args.Length > 1)
        {
            try { Promo.Run(args[1]); }
            catch (Exception ex) { File.WriteAllText(Path.Combine(args[1], "promo-error.txt"), ex.ToString()); }
            return;
        }
        if (arg == "--icontest" && args.Length > 1)
        {
            // Self-test: the Desktop icon in each look, over a colourful "wallpaper"
            var list = new List<Item>();
            foreach (var p in new[] { @"C:\Windows\System32\notepad.exe", @"C:\Windows\System32\calc.exe", @"C:\Windows\explorer.exe" })
                list.Add(new Item { Path = p, Icon = Native.GetIcon(p) });
            using (var bmp = new Bitmap(4 * 150 + 10, 160))
            using (var g = Graphics.FromImage(bmp))
            {
                using (var b = new LinearGradientBrush(new Rectangle(0, 0, bmp.Width, bmp.Height), Color.FromArgb(40, 90, 200), Color.FromArgb(230, 120, 160), 0f)) g.FillRectangle(b, 0, 0, bmp.Width, bmp.Height);
                int x = 10;
                foreach (bool light in new[] { false, true })
                    foreach (bool glassy in new[] { true, false })
                    {
                        Theme.Light = light; Theme.Glass = glassy;
                        using (var icon = FolderForm.RenderPreview(list, 128)) g.DrawImage(icon, x, 16, 128, 128);
                        x += 150;
                    }
                bmp.Save(args[1], ImageFormat.Png);
            }
            return;
        }
        if (arg == "--update-now")
        {
            // Command line: update now if GitHub has a newer version (what the settings button does)
            try
            {
                var r = Updater.FetchLatest();
                Updater.Remember(r);
                if (r.Version > Updater.Current) Updater.Install(Updater.Download(r.ExeUrl, null));
                else HoverWatcher.Log("update-now: already up to date (" + Updater.CurrentText + ")");
            }
            catch (Exception ex) { HoverWatcher.Log("update-now failed: " + ex.Message); }
            return;
        }
        if (arg == "--updatetest" && args.Length > 1)
        {
            // Self-test: reach GitHub, read the latest release, download (but don't install) its exe
            var sb = new StringBuilder("this build: " + Updater.CurrentText + "\r\n");
            try
            {
                var r = Updater.FetchLatest();
                sb.AppendLine("latest on GitHub: " + r.Version.ToString(3) + "  " + r.ExeUrl);
                sb.AppendLine("newer than this build: " + (r.Version > Updater.Current));
                string exe = Updater.Download(r.ExeUrl, null);
                sb.AppendLine("downloaded " + new FileInfo(exe).Length + " bytes, looks like a program: yes");
                File.Delete(exe);
            }
            catch (Exception ex) { sb.AppendLine("FAILED: " + ex.Message); }
            File.WriteAllText(args[1], sb.ToString());
            return;
        }
        if (arg == "--edgetest2" && args.Length > 1)
        {
            // Self-test: a real folder with its settings card over stripes; saves the card zoomed 4x
            Theme.Load();
            string dir = Path.Combine(Path.GetTempPath(), "PrysmEdge");
            Directory.CreateDirectory(dir);
            string file = Path.Combine(dir, "Edge" + Installer.Ext);
            FolderForm.WriteFolderFile(file, FolderForm.RenderPreview(new List<Item>(), 256),
                "title=Edge\r\nitem=C:\\Windows\\System32\\notepad.exe|Notepad\r\nitem=C:\\Windows\\System32\\calc.exe|Calculator\r\n");
            var area = Screen.PrimaryScreen.WorkingArea;
            var stripes = new Form { FormBorderStyle = FormBorderStyle.None, StartPosition = FormStartPosition.Manual,
                                     Bounds = new Rectangle(area.Left + 20, area.Top + 20, 760, 560), ShowInTaskbar = false, TopMost = true };
            stripes.Paint += (s, e) =>
            {
                for (int i = -600; i < 1400; i += 14)
                    using (var p = new Pen(i % 28 == 0 ? Color.White : Color.FromArgb(30, 60, 200), 7)) e.Graphics.DrawLine(p, i, 0, i + 500, 600);
            };
            stripes.Show();
            var f = new FolderForm("edge", file) { OpenAt = new Point(area.Left + 50, area.Top + 50) };
            f.Show();
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < 700) { Application.DoEvents(); Thread.Sleep(10); }
            f.TestShowSettings();
            sw.Restart();
            while (sw.ElapsedMilliseconds < 900) { Application.DoEvents(); Thread.Sleep(10); }
            Form card = null;
            foreach (Form o in Application.OpenForms) if (o is SettingsCard) card = o;
            var grab = card != null ? card.Bounds : f.Bounds;
            using (var shot = new Bitmap(grab.Width, grab.Height))
            {
                using (var g = Graphics.FromImage(shot)) g.CopyFromScreen(grab.Location, Point.Empty, grab.Size);
                using (var big = new Bitmap(grab.Width * 4, grab.Height * 4))
                using (var g = Graphics.FromImage(big))
                {
                    g.InterpolationMode = InterpolationMode.NearestNeighbor;
                    g.PixelOffsetMode = PixelOffsetMode.Half;
                    g.DrawImage(shot, 0, 0, big.Width, big.Height);
                    big.Save(args[1], ImageFormat.Png);
                }
            }
            var fr = f.Bounds;
            using (var shot = new Bitmap(fr.Width, fr.Height))
            {
                using (var g = Graphics.FromImage(shot)) g.CopyFromScreen(fr.Location, Point.Empty, fr.Size);
                shot.Save(args[1].Replace(".png", ".folder.png"), ImageFormat.Png);
            }
            if (card != null) card.Dispose();
            f.Dispose();
            stripes.Close();
            try { File.Delete(file); } catch { }
            return;
        }
        if (arg == "--edgetest" && args.Length > 1)
        {
            // Self-test: shows a glass card for a moment and saves its four corners, zoomed 8x
            Theme.Load();
            var area = Screen.PrimaryScreen.WorkingArea;
            var rect = new Rectangle(area.Left + 40, area.Top + 40, 260, 160);
            // a busy, high-contrast background right behind it (worst case for edges)
            var stripes = new Form { FormBorderStyle = FormBorderStyle.None, StartPosition = FormStartPosition.Manual, Bounds = Rectangle.Inflate(rect, 30, 30), ShowInTaskbar = false, TopMost = true };
            stripes.Paint += (s, e) =>
            {
                for (int i = -400; i < 800; i += 14)
                    using (var p = new Pen(i % 28 == 0 ? Color.White : Color.FromArgb(30, 60, 200), 7)) e.Graphics.DrawLine(p, i, 0, i + 300, 400);
            };
            stripes.Show();
            using (var host = new Form { FormBorderStyle = FormBorderStyle.None, StartPosition = FormStartPosition.Manual, Bounds = rect,
                                         ShowInTaskbar = false, TopMost = true, BackColor = Theme.Key, TransparencyKey = Theme.Key })
            {
                host.Show();
                var gb = new GlassBackdrop(host, 24) { Bounds = rect };
                gb.Show();
                host.BringToFront();
                var sw = Stopwatch.StartNew();
                while (sw.ElapsedMilliseconds < 900) { Application.DoEvents(); Thread.Sleep(10); }
                int c = 34, z = 8;
                var grab = Rectangle.Inflate(rect, 6, 6);
                using (var shot = new Bitmap(grab.Width, grab.Height))
                {
                    using (var g = Graphics.FromImage(shot)) g.CopyFromScreen(grab.Location, Point.Empty, grab.Size);
                    using (var outBmp = new Bitmap(2 * c * z + 8, 2 * c * z + 8))
                    using (var g = Graphics.FromImage(outBmp))
                    {
                        g.Clear(Color.Magenta);
                        g.InterpolationMode = InterpolationMode.NearestNeighbor;
                        g.PixelOffsetMode = PixelOffsetMode.Half;
                        var corners = new[] { new Point(0, 0), new Point(shot.Width - c, 0), new Point(0, shot.Height - c), new Point(shot.Width - c, shot.Height - c) };
                        for (int i = 0; i < 4; i++)
                            g.DrawImage(shot, new Rectangle((i % 2) * (c * z + 8), (i / 2) * (c * z + 8), c * z, c * z), new Rectangle(corners[i], new Size(c, c)), GraphicsUnit.Pixel);
                        outBmp.Save(args[1], ImageFormat.Png);
                    }
                }
                gb.Close(); host.Close();
            }
            stripes.Close();
            return;
        }
        if (arg == "--glasstest" && args.Length > 1)
        {
            // Self-test: can this PC do Prism's rounded glass, or only the plain-acrylic fallback?
            using (var host = new Form { StartPosition = FormStartPosition.Manual, Location = new Point(-30000, -30000), Size = new Size(300, 200), ShowInTaskbar = false })
            {
                host.Show();
                var gb = new GlassBackdrop(host, 24);
                gb.Location = host.Location;
                gb.Show();
                Application.DoEvents();
                File.WriteAllText(args[1], (gb.Rounded ? "rounded host-backdrop glass: OK" : "fallback: plain acrylic") +
                                           (gb.Masked ? ", smooth mask: OK" : ", smooth mask: NOT available (clip fallback)"));
                gb.Close(); host.Close();
            }
            return;
        }
        if (arg == "--snapshot" && args.Length > 2)
        {
            try { Snapshot(args[1], args[2]); }
            catch (Exception ex) { File.WriteAllText(args[2] + ".error.txt", ex.ToString()); }
            return;
        }
        if (arg == "--install")
        {
            Installer.Install(quiet);
            if (Array.IndexOf(args, "--updated") >= 0)
            {
                // installed by the in-app updater: say so, and tidy up the downloaded copy afterwards
                Settings.NotifiedVersion = Updater.CurrentText;
                var done = new Toast.Done("Prysm Folder is up to date", "You're now on version " + Updater.CurrentText + ".");
                Application.Run(done);
                try
                {
                    Process.Start(new ProcessStartInfo("cmd.exe", "/c ping 127.0.0.1 -n 3 > nul & del \"" + Application.ExecutablePath + "\"")
                    { CreateNoWindow = true, UseShellExecute = false });
                }
                catch { }
            }
            return;
        }
        if (arg == "--uninstall") { Installer.Uninstall(quiet); return; }
        if (arg == "--machine-register")
        {
            try { Installer.MachineRegister(); }
            catch (Exception ex) { MessageBox.Show("Couldn't add " + Installer.AppName + " to the New menu for this PC:\n" + ex.Message, Installer.AppName);Environment.Exit(2); }
            return;
        }
        if (arg == "--machine-unregister") { Installer.MachineUnregister(); return; }
        if (arg == "--new")
        {
            // From the right-click menu: make the folder where you clicked, open it, name it.
            string dir = args.Length > 1 ? args[1].Trim().Trim('"') : null;
            try { arg = Installer.CreateFolderFile(dir, "New " + Installer.AppName); }
            catch (Exception ex) { MessageBox.Show("Couldn't create the folder:\n" + ex.Message, Installer.AppName);return; }
            FolderForm.StartRenaming = true;
        }

        // Double-clicking the downloaded exe offers to install it.
        if (arg.Length == 0 && !Installer.RunningInstalled)
        {
            var r = MessageBox.Show("Install " + Installer.AppName + "?\n\nThis adds \"" + Installer.AppName + "\" to the New menu when you right-click the Desktop, so you can make phone-style app folders right there.\n\nWindows will ask once for permission (needed for the Windows 11 New menu).",
                Installer.AppName, MessageBoxButtons.YesNo, MessageBoxIcon.Question);
            if (r == DialogResult.Yes) Installer.Install(false);
            return;
        }

        string filePath = null, id;
        if (arg.EndsWith(Installer.Ext, StringComparison.OrdinalIgnoreCase) && File.Exists(arg))
        {
            filePath = Path.GetFullPath(arg);
            id = "file:" + filePath.ToLowerInvariant().Replace('\\', '/');
        }
        else id = arg.Length > 0 ? arg : "default";

        // --at x,y: reopen at this spot (after switching look in Settings)
        Point? at = null;
        int ai = Array.IndexOf(args, "--at");
        if (ai >= 0 && ai + 1 < args.Length)
        {
            var xy = args[ai + 1].Split(',');
            int x, y;
            if (xy.Length == 2 && int.TryParse(xy[0], out x) && int.TryParse(xy[1], out y)) at = new Point(x, y);
        }

        if (Array.IndexOf(args, "--refresh-icons") >= 0) FolderForm.RefreshAllIcons();

        // opening a folder brings the hover helper back if it isn't running for some reason
        if (filePath != null && Installer.IsInstalled && Installer.RunningInstalled && !HoverWatcher.IsRunning)
        {
            HoverWatcher.Log("hover helper wasn't running: restarting it");
            HoverWatcher.StartDetached();
        }

        // Files dropped on the folder's Desktop icon arrive after the folder itself (shell drop handler).
        var dropped = new List<string>();
        if (filePath != null)
            for (int i = 1; i < args.Length; i++)
            {
                string a = args[i].Trim().Trim('"');
                if (a.StartsWith("--")) { if (a == "--at") i++; continue; }
                if (a.Length > 0 && (File.Exists(a) || Directory.Exists(a) || Native.IsUrl(a))) dropped.Add(a);
            }

        bool created;
        using (var mutex = new Mutex(true, "AppFolder_" + id, out created))
        {
            if (!created && at.HasValue)
            {
                // the window we're replacing is still closing: wait for it
                try { created = mutex.WaitOne(3000); } catch (AbandonedMutexException) { created = true; }
            }
            if (!created)
            {
                // already open in another window: add them to the file; that window keeps them when it saves
                if (dropped.Count > 0) using (var f = new FolderForm(id, filePath)) f.AddDropped(dropped);
                return;
            }
            var form = new FolderForm(id, filePath) { HoverMode = FolderForm.OpenedByHover, OpenAt = at };
            if (dropped.Count > 0) form.AddDropped(dropped);   // then it opens showing what you just added
            Application.AddMessageFilter(new InteractionFilter());
            Application.Run(form);
        }
    }

    // Test helper: draws a folder window into a PNG without showing it on screen.
    static void Snapshot(string folderFile, string outPng)
    {
        Decor.Disabled = true;
        using (var f = new FolderForm("snapshot", Path.GetFullPath(folderFile)))
        {
            f.ShowInTaskbar = false;
            f.Location = new Point(-30000, -30000);
            f.Show();
            f.Location = new Point(-30000, -30000);
            Application.DoEvents();
            using (var bmp = new Bitmap(f.Width, f.Height))
            {
                f.DrawToBitmap(bmp, new Rectangle(0, 0, f.Width, f.Height));
                bmp.Save(outPng, ImageFormat.Png);
            }
            using (var card = new SettingsCard(f, g => { }) { Location = new Point(-30000, -30000) })
            using (var bmp = new Bitmap(card.Width, card.Height))
            {
                card.DrawToBitmap(bmp, new Rectangle(0, 0, card.Width, card.Height));
                bmp.Save(outPng.Replace(".png", ".card.png"), ImageFormat.Png);
            }
            using (var m = new PrismMenu(f))
            {
                m.Add("Open", '', null);
                m.Add("Run as administrator", '', null);
                m.Add("Open file location", '', null);
                m.AddSeparator();
                m.Add("Rename", '', null);
                m.Add("Move right", '', null);
                m.AddSeparator();
                m.Add("Remove from folder", '', null, true);
                m.Measure();
                using (var bmp = new Bitmap(m.Width, m.Height))
                {
                    m.DrawToBitmap(bmp, new Rectangle(0, 0, m.Width, m.Height));
                    bmp.Save(outPng.Replace(".png", ".menu.png"), ImageFormat.Png);
                }
            }
            using (var pc = new PromptCard(f, "Rename", "Name shown under the icon", "Calculator"))
            using (var bmp = new Bitmap(pc.Width, pc.Height))
            {
                pc.DrawToBitmap(bmp, new Rectangle(0, 0, pc.Width, pc.Height));
                bmp.Save(outPng.Replace(".png", ".prompt.png"), ImageFormat.Png);
            }
            f.Close();
        }
    }
}
