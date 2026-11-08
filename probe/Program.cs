using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using Microsoft.Win32;

// Measures how Windows behaves around a virtual desktop switch, for Lanes' design:
// wallpapers per desktop, Windows' own "Desktop N" pill, a live picture of the desktop icons,
// and the taskbar setting that shows windows of all desktops.
static class P
{
    const string Dll = "VirtualDesktopAccessor.dll";
    [DllImport(Dll)] static extern int GetCurrentDesktopNumber();
    [DllImport(Dll)] static extern int GetDesktopCount();
    [DllImport(Dll)] static extern Guid GetDesktopIdByNumber(int n);
    [DllImport(Dll)] static extern int GoToDesktopNumber(int n);
    [DllImport(Dll)] static extern int CreateDesktop();
    [DllImport(Dll)] static extern int PinWindow(IntPtr h);
    [DllImport(Dll)] static extern int MoveWindowToDesktopNumber(IntPtr h, int n);

    [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr h);
    [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr h, IntPtr dc);
    [DllImport("gdi32.dll")] static extern bool BitBlt(IntPtr dst, int x, int y, int w, int h, IntPtr src, int sx, int sy, int rop);
    [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleBitmap(IntPtr dc, int w, int h);
    [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr dc, IntPtr o);
    [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr o);
    [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr dc);
    [DllImport("user32.dll")] static extern bool SetWindowDisplayAffinity(IntPtr h, uint a);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr FindWindow(string cls, string title);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string cls, string title);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr SendMessageTimeout(IntPtr h, int msg, IntPtr wp, string lp, int flags, int timeout, out IntPtr result);
    [DllImport("user32.dll")] static extern bool SystemParametersInfo(int a, int b, out RECT r, int c);
    [DllImport("dwmapi.dll")] static extern int DwmRegisterThumbnail(IntPtr dest, IntPtr src, out IntPtr thumb);
    [DllImport("dwmapi.dll")] static extern int DwmUpdateThumbnailProperties(IntPtr thumb, ref THUMB props);
    [DllImport("dwmapi.dll")] static extern int DwmQueryThumbnailSourceSize(IntPtr thumb, out SIZE size);
    [DllImport("dwmapi.dll")] static extern int DwmUnregisterThumbnail(IntPtr thumb);
    [DllImport("dwmapi.dll")] static extern int DwmFlush();
    [DllImport("winmm.dll")] static extern uint timeBeginPeriod(uint ms);
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; public override string ToString() => $"({L},{T})-({R},{B})"; }
    [StructLayout(LayoutKind.Sequential)] public struct SIZE { public int W, H; }
    [StructLayout(LayoutKind.Sequential)] public struct THUMB { public uint Flags; public RECT Dest, Src; public byte Opacity; public int Visible, ClientOnly; }

    static string Out = "probe-out";
    static readonly Stopwatch Clock = Stopwatch.StartNew();
    static StreamWriter LogW;
    static void L(string s)
    {
        string line = $"[{Clock.ElapsedMilliseconds,7}] {s}";
        LogW.WriteLine(line); LogW.Flush(); Console.WriteLine(line);
    }

    static readonly Color[] Walls = { Color.FromArgb(150, 40, 40), Color.FromArgb(40, 60, 150), Color.FromArgb(40, 130, 60) };

    [STAThread]
    static void Main(string[] a)
    {
        Out = a.Length > 0 ? a[0] : Out;
        Directory.CreateDirectory(Out);
        LogW = new StreamWriter(Path.Combine(Out, a.Length > 1 ? $"log-{a[1]}.txt" : "log.txt"));
        timeBeginPeriod(1);
        Application.EnableVisualStyles();
        try
        {
            if (a.Length > 1 && a[1] == "setup") Setup(); else Run();
        }
        catch (Exception e) { L("FAIL " + e); }
        LogW.Close();
    }

    static void Setup()
    {
        while (GetDesktopCount() < 3) CreateDesktop();
        L($"desktops {GetDesktopCount()}");
        for (int i = 0; i < 3; i++)
        {
            string path = Path.Combine(Path.GetFullPath(Out), $"wall{i}.png");
            using (var bmp = new Bitmap(1920, 1080))
            using (var g = Graphics.FromImage(bmp))
            {
                g.Clear(Walls[i]);
                // a small mark so the picture is not a single colour
                g.FillEllipse(Brushes.White, 1700, 800, 80, 80);
                bmp.Save(path, ImageFormat.Png);
            }
            var id = GetDesktopIdByNumber(i);
            using var key = Registry.CurrentUser.CreateSubKey($@"Software\Microsoft\Windows\CurrentVersion\Explorer\VirtualDesktops\Desktops\{{{id.ToString().ToUpperInvariant()}}}");
            key.SetValue("Wallpaper", path);
            L($"desktop {i} {id} wallpaper {path}");
        }
        string desk = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        foreach (var n in new[] { "Notes.txt", "Report.txt", "Todo.txt" }) File.WriteAllText(Path.Combine(desk, n), "x");
        Directory.CreateDirectory(Path.Combine(desk, "Photos"));
        L($"icons made in {desk}");
    }

    static void Wait(int ms)
    {
        var until = Clock.ElapsedMilliseconds + ms;
        while (Clock.ElapsedMilliseconds < until) { Application.DoEvents(); Thread.Sleep(2); }
    }

    static Bitmap Grab(int x, int y, int w, int h)
    {
        var bmp = new Bitmap(w, h, PixelFormat.Format32bppRgb);
        using var g = Graphics.FromImage(bmp);
        IntPtr dst = g.GetHdc(), src = GetDC(IntPtr.Zero);
        BitBlt(dst, 0, 0, w, h, src, x, y, 0x00CC0020);
        ReleaseDC(IntPtr.Zero, src);
        g.ReleaseHdc(dst);
        return bmp;
    }

    static void Shot(string name)
    {
        using var b = Grab(0, 0, 1920, 1080);
        b.Save(Path.Combine(Out, name + ".png"), ImageFormat.Png);
        L($"screenshot {name}.png");
    }

    static string Name(Color c)
    {
        for (int i = 0; i < Walls.Length; i++)
            if (Math.Abs(c.R - Walls[i].R) < 12 && Math.Abs(c.G - Walls[i].G) < 12 && Math.Abs(c.B - Walls[i].B) < 12) return $"wall{i}";
        if (c.R > 230 && c.B > 230 && c.G < 40) return "magenta";
        return $"#{c.R:X2}{c.G:X2}{c.B:X2}";
    }

    static RECT Work()
    {
        SystemParametersInfo(0x0030, 0, out var r, 0);
        return r;
    }

    sealed class Cover : Form
    {
        readonly bool _active;
        public Cover(Color c, Rectangle r, bool active = false)
        {
            _active = active;
            FormBorderStyle = FormBorderStyle.None; ShowInTaskbar = false; TopMost = true; BackColor = c;
            StartPosition = FormStartPosition.Manual; Bounds = r;
        }
        protected override bool ShowWithoutActivation => !_active;
        protected override CreateParams CreateParams
        {
            get { var p = base.CreateParams; p.ExStyle |= 0x80 | 0x8 | (_active ? 0 : 0x08000000); return p; }
        }
    }

    sealed class Plain : Form
    {
        public Plain(string title, Color c, Rectangle r)
        {
            Text = title; BackColor = c; StartPosition = FormStartPosition.Manual; Bounds = r;
        }
    }

    // A dark #2C2C2C box near the bottom centre: Windows' pill. Returns its bounds in screen pixels or null.
    static Rectangle? FindPill(Bitmap strip, int ox, int oy)
    {
        int minX = int.MaxValue, minY = int.MaxValue, maxX = -1, maxY = -1, n = 0;
        var d = strip.LockBits(new Rectangle(0, 0, strip.Width, strip.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppRgb);
        unsafe
        {
            for (int y = 0; y < strip.Height; y++)
            {
                byte* row = (byte*)d.Scan0 + y * d.Stride;
                for (int x = 0; x < strip.Width; x++)
                {
                    byte b = row[x * 4], g = row[x * 4 + 1], r = row[x * 4 + 2];
                    if (Math.Abs(r - 44) <= 3 && Math.Abs(g - 44) <= 3 && Math.Abs(b - 44) <= 3)
                    {
                        n++;
                        if (x < minX) minX = x; if (x > maxX) maxX = x; if (y < minY) minY = y; if (y > maxY) maxY = y;
                    }
                }
            }
        }
        strip.UnlockBits(d);
        if (n < 400) return null;
        return new Rectangle(ox + minX, oy + minY, maxX - minX + 1, maxY - minY + 1);
    }

    // Switches and watches the screen: the colour in the middle (the wallpaper) and the pill.
    static void Watch(string name, int to, int ms, bool savePill = false)
    {
        var w = Work();
        int sx = 660, sy = Math.Max(0, w.B - 260), sw = 600, sh = Math.Min(260, 1080 - sy);
        var t0 = Clock.Elapsed.TotalMilliseconds;
        GoToDesktopNumber(to);
        string lastMid = "", lastPill = "";
        int frames = 0;
        bool savedFirst = false, savedLate = false;
        Rectangle? last = null;
        while (Clock.Elapsed.TotalMilliseconds - t0 < ms)
        {
            Application.DoEvents();
            double t = Clock.Elapsed.TotalMilliseconds - t0;
            using var mid = Grab(960, 400, 1, 1);
            string m = Name(mid.GetPixel(0, 0));
            using var strip = Grab(sx, sy, sw, sh);
            var pill = FindPill(strip, sx, sy);
            string ps = pill is { } p ? $"{p.X},{p.Y} {p.Width}x{p.Height}" : "none";
            frames++;
            if (m != lastMid) { L($"  {name} +{t,6:0}ms middle {m}"); lastMid = m; }
            if (ps != lastPill) { L($"  {name} +{t,6:0}ms pill {ps}"); lastPill = ps; }
            if (savePill && pill is { } q)
            {
                if (!savedFirst && t > 400) { Save(strip, $"{name}-pill-early"); savedFirst = true; }
                if (!savedLate && last is { } lp && q.Y > lp.Y) { Save(strip, $"{name}-pill-moving"); savedLate = true; }
            }
            last = pill;
        }
        L($"  {name}: {frames} samples; desktop now {GetCurrentDesktopNumber()}");

        static void Save(Bitmap b, string n) { b.Save(Path.Combine(Out, n + ".png"), ImageFormat.Png); L($"  saved {n}.png"); }
    }

    static void Run()
    {
        var work = Work();
        L($"desktops {GetDesktopCount()}, current {GetCurrentDesktopNumber()}, work area {work}");
        GoToDesktopNumber(0);
        Wait(3500);
        Shot("00-desktop0");

        // ---- 1. wallpaper and pill after a switch, nothing on top
        L("===== 1. switch with nothing on top");
        Watch("plain-0to1", 1, 3500, savePill: true);
        Shot("01-desktop1-after");
        Watch("plain-1to2", 2, 3500);
        Watch("plain-2to0", 0, 3500);

        // ---- 2. the same under a full-screen topmost cover that is left out of screenshots
        L("===== 2. switch under a cover excluded from capture");
        var cover = new Cover(Color.Black, new Rectangle(0, 0, 1920, 1080));
        cover.Show();
        PinWindow(cover.Handle);
        L($"  exclude from capture: {SetWindowDisplayAffinity(cover.Handle, 0x11)}");
        Wait(1500);
        Watch("covered-0to1", 1, 3000);
        Watch("covered-1to2", 2, 3000);
        Watch("covered-2to0", 0, 3000);
        cover.Close();
        Wait(1500);

        // ---- 3. is the pill drawn above a topmost window?
        L("===== 3. pill over a visible topmost cover");
        var magenta = new Cover(Color.Magenta, new Rectangle(0, 0, 1920, 1080));
        magenta.Show();
        PinWindow(magenta.Handle);
        Wait(1500);
        Watch("magenta-0to1", 1, 2500, savePill: true);
        Shot("03-pill-over-magenta");
        magenta.Close();
        GoToDesktopNumber(0);
        Wait(3500);

        // ---- 4. a live picture of the desktop icons
        L("===== 4. picture of the desktop (icons)");
        var host = new Cover(Color.Magenta, new Rectangle(0, 0, 1920, 1080));
        host.Show();
        PinWindow(host.Handle);
        Wait(800);
        IntPtr progman = FindWindow("Progman", null);
        IntPtr defView = FindWindowEx(progman, IntPtr.Zero, "SHELLDLL_DefView", null);
        IntPtr workerWithView = IntPtr.Zero, worker = IntPtr.Zero;
        while ((worker = FindWindowEx(IntPtr.Zero, worker, "WorkerW", null)) != IntPtr.Zero)
            if (FindWindowEx(worker, IntPtr.Zero, "SHELLDLL_DefView", null) != IntPtr.Zero) workerWithView = worker;
        L($"  Progman {progman:X} (view inside {defView:X}); WorkerW holding the view {workerWithView:X}");
        int cell = 0;
        foreach (var (label, src) in new[] { ("Progman", progman), ("WorkerW", workerWithView) })
        {
            if (src == IntPtr.Zero) { L($"  {label}: none"); continue; }
            int hr = DwmRegisterThumbnail(host.Handle, src, out var th);
            DwmQueryThumbnailSourceSize(th, out var size);
            int x = 40 + cell * 940;
            var props = new THUMB { Flags = 0x1 | 0x8 | 0x4, Dest = new RECT { L = x, T = 200, R = x + 900, B = 200 + 506 }, Visible = 1, Opacity = 255 };
            int hr2 = DwmUpdateThumbnailProperties(th, ref props);
            L($"  {label}: register 0x{hr:X8}, update 0x{hr2:X8}, source {size.W}x{size.H}, drawn at x {x}");
            cell++;
        }
        Wait(1200);
        Shot("04-desktop-thumbnails");
        host.Close();
        Wait(800);

        // ---- 5. taskbar: windows of all desktops
        L("===== 5. taskbar shows windows of all desktops");
        var a1 = new Plain("Alpha (desktop 2)", Color.SteelBlue, new Rectangle(200, 200, 600, 400));
        var a2 = new Plain("Beta (desktop 3)", Color.SeaGreen, new Rectangle(300, 300, 600, 400));
        var a0 = new Plain("Here (desktop 1)", Color.DarkOrange, new Rectangle(1000, 200, 600, 400));
        a1.Show(); a2.Show(); a0.Show();
        Wait(800);
        L($"  move: {MoveWindowToDesktopNumber(a1.Handle, 1)} {MoveWindowToDesktopNumber(a2.Handle, 2)}");
        Wait(1500);
        string adv = @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced";
        using (var k = Registry.CurrentUser.OpenSubKey(adv)) L($"  VirtualDesktopTaskbarFilter before: {k?.GetValue("VirtualDesktopTaskbarFilter") ?? "(none)"}; AltTab {k?.GetValue("VirtualDesktopAltTabFilter") ?? "(none)"}");
        TaskbarShot("05a-taskbar-before");
        using (var k = Registry.CurrentUser.CreateSubKey(adv)) k.SetValue("VirtualDesktopTaskbarFilter", 0, RegistryValueKind.DWord);
        SendMessageTimeout((IntPtr)0xFFFF, 0x001A, IntPtr.Zero, "TraySettings", 2, 3000, out _);
        Wait(2000);
        TaskbarShot("05b-taskbar-after-traysettings");
        L($"  immersive notify: {Notify()}");
        Wait(2000);
        TaskbarShot("05c-taskbar-after-immersive");
        using (var k = Registry.CurrentUser.CreateSubKey(adv)) k.SetValue("VirtualDesktopTaskbarFilter", 1, RegistryValueKind.DWord);
        L($"  back to this desktop only: {Notify()}");
        Wait(2000);
        TaskbarShot("05d-taskbar-back");
        Shot("05e-screen");
        L("done");
    }

    static void TaskbarShot(string name)
    {
        RECT w = Work();
        int top = w.B < 1080 ? w.B : 0, h = w.B < 1080 ? 1080 - w.B : w.T;
        using var b = Grab(0, top, 1920, Math.Max(1, h));
        b.Save(Path.Combine(Out, name + ".png"), ImageFormat.Png);
        L($"  taskbar {name}.png ({top}, {h} high)");
    }

    static string Notify()
    {
        try
        {
            var shell = (IServiceProvider)Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("c2f03a33-21f5-47fa-b4bb-156362a2f239"))!)!;
            var service = new Guid("53660488-8855-460b-a9ab-5cfc6b5012ca");
            var iid = new Guid("4214f6fa-eb36-4e2f-9ca2-23fdc1832df7");
            int hr = shell.QueryService(ref service, ref iid, out IntPtr p);
            if (hr != 0) return $"QueryService 0x{hr:X8}";
            var cache = (IImmersiveSettingsCache)Marshal.GetObjectForIUnknown(p);
            for (int id = 0; id < 0x80; id++) cache.OnSettingChanged(id);
            return "ok";
        }
        catch (Exception e) { return e.Message; }
    }

    [ComImport, Guid("4214f6fa-eb36-4e2f-9ca2-23fdc1832df7"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IImmersiveSettingsCache { void OnSettingChanged(int id); }

    [ComImport, Guid("6d5140c1-7436-11ce-8034-00aa006009fa"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IServiceProvider { [PreserveSig] int QueryService(ref Guid service, ref Guid riid, out IntPtr ppv); }
}
