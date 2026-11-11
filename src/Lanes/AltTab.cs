using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using Lanes.Core;
using static Lanes.Native;

namespace Lanes;

// Alt+Tab as Cmd+Tab on a Mac, in place of Windows' own: a row of large app icons in the middle
// of the screen, the apps of every desktop in the order they were last used; letting go of Alt
// goes to the one picked, on whatever desktop it is. The keys come from WinTab's keyboard hook,
// which only decides what to keep from Windows and passes the rest to a thread of this switcher's
// own: that thread keeps the order apps were used in, the icons, and draws the panel.
sealed class AltTab
{
    const int Begin = WM_USER + 1, Move = WM_USER + 2, Go = WM_USER + 3, Cancel = WM_USER + 4;
    const int ShowTimer = 1, FadeTimer = 2, PreloadTimer = 3;
    const float Icon = 96, Pad = 12, Margin = 16, Band = 30, Radius = 22;

    readonly Engine _engine;
    readonly RecentWindows _recent = new();
    readonly Dictionary<string, (Bitmap? Icon, string Name)> _icons = new(StringComparer.OrdinalIgnoreCase);
    readonly ManualResetEventSlim _ready = new();
    Panel _panel = null!;
    WinEventProc? _proc;
    IntPtr _window;

    // The keyboard hook's own state: whether this switcher has the keys.
    bool _on;

    // The switcher thread's: the apps on show, the one picked, the monitor and scale they are for.
    List<SwitchApp>? _apps;
    int _at;
    MONITORINFO _monitor;
    float _scale = 1;
    bool _light;
    double _fadeFrom;

    public IntPtr Handle => _window;

    public AltTab(Engine engine)
    {
        _engine = engine;
        var t = new Thread(Run) { IsBackground = true, Name = "alt+tab" };
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        _ready.Wait(5000);
    }

    // ---------- the keys, on the keyboard hook's thread ----------

    // Whether to keep this key from Windows. Quick: everything else is posted to the switcher.
    public bool OnKey(int vk, bool down)
    {
        if (_window == IntPtr.Zero) return false;
        // Alt let go where the hook could not see it (a secure desktop): start over.
        if (_on && !Held(0x12)) { _on = false; Tell(Cancel); }
        if (!_on)
        {
            if (vk != 0x09 || !down || !Held(0x12) || Held(0x11) || Held(0x5B) || Held(0x5C) || _engine.Busy) return false;
            _on = true;
            // Alt let go with no other key would open the menu bar of the app in front.
            keybd_event(0xE8, 0, 0, IntPtr.Zero);
            keybd_event(0xE8, 0, 2 /* KEYEVENTF_KEYUP */, IntPtr.Zero);
            Tell(Begin, Held(0x10) ? -1 : 1);
            return true;
        }
        switch (vk)
        {
            case 0x09:
                if (down) Tell(Move, Held(0x10) ? -1 : 1);
                return true;
            case 0x25 or 0x27: // left, right
                if (down) Tell(Move, vk == 0x25 ? -1 : 1);
                return true;
            case 0x1B:
                if (down) { _on = false; Tell(Cancel); }
                return true;
            case 0x12 or 0xA4 or 0xA5:
                if (!down) { _on = false; Tell(Go); }
                return false;
        }
        return false;
    }

    void Tell(int what, int arg = 0) => PostMessage(_window, what, (IntPtr)arg, IntPtr.Zero);

    static bool Held(int vk) => GetAsyncKeyState(vk) < 0;

    // ---------- the switcher's thread ----------

    void Run()
    {
        try
        {
            _panel = new Panel(this);
            _window = _panel.Handle;
            _proc = OnForeground;
            SetWinEventHook(0x0003 /* EVENT_SYSTEM_FOREGROUND */, 0x0003, IntPtr.Zero, _proc, 0, 0, 0 /* WINEVENT_OUTOFCONTEXT */ | 2 /* WINEVENT_SKIPOWNPROCESS */);
            _recent.Front(GetAncestor(GetForegroundWindow(), GA_ROOTOWNER));
            SetTimer(_window, PreloadTimer, 3000, IntPtr.Zero);
        }
        catch (Exception e) { Log.Write($"alt+tab: {e}"); }
        finally { _ready.Set(); }
        while (MessageLoop.GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
        {
            MessageLoop.TranslateMessage(ref msg);
            MessageLoop.DispatchMessage(ref msg);
        }
    }

    void OnForeground(IntPtr hook, uint ev, IntPtr hwnd, int obj, int child, uint thread, uint time) =>
        _recent.Front(GetAncestor(hwnd, GA_ROOTOWNER));

    void OnMessage(int msg, int arg)
    {
        try
        {
            switch (msg)
            {
                case Begin: Start(back: arg < 0); break;
                case Move: Step(arg); break;
                case Go: Finish(go: true); break;
                case Cancel: Finish(go: false); break;
                case 0x0113 /* WM_TIMER */ when arg == ShowTimer:
                    KillTimer(_window, ShowTimer);
                    if (_apps != null) Show();
                    break;
                case 0x0113 when arg == FadeTimer: Fade(); break;
                case 0x0113 when arg == PreloadTimer:
                    if (_apps == null) foreach (var a in Apps()) IconOf(a);
                    break;
            }
        }
        catch (Exception e) { Log.Write($"alt+tab: {e}"); }
    }

    // The app windows on every desktop, minimized ones too, as apps in the order they were used.
    List<SwitchApp> Apps()
    {
        // Front to back.
        var windows = AppWindows.All().AsEnumerable().Reverse().Select(h => (h, AppIds.Of(h) ?? AppWindows.AppName(h))).ToList();
        _recent.Keep(windows.Select(w => w.Item1));
        return AppSwitch.Apps(windows, _recent.Order);
    }

    void Start(bool back)
    {
        _apps = Apps();
        _at = AppSwitch.First(_apps.Count, back);
        if (_apps.Count == 0) { _apps = null; return; }
        // Shown only if Alt is still held a moment later, so a quick Alt+Tab does not flash it.
        SetTimer(_window, ShowTimer, 100, IntPtr.Zero);
    }

    void Step(int by)
    {
        if (_apps == null) return;
        _at = AppSwitch.Step(_at, by, _apps.Count);
        if (_panel.Shown) Draw();
    }

    void Finish(bool go)
    {
        KillTimer(_window, ShowTimer);
        KillTimer(_window, FadeTimer);
        _panel.Hide();
        var apps = _apps;
        _apps = null;
        if (!go || apps == null) return;
        var app = apps[_at];
        Log.Write($"alt+tab: to {IconOf(app).Name} ({apps.Count} apps)");
        _engine.Bring(app.Window);
    }

    void Show()
    {
        GetCursorPos(out var pt);
        IntPtr monitor = MonitorFromPoint(pt, MONITOR_DEFAULTTONEAREST);
        _monitor = MonitorOf(monitor);
        _scale = GetDpiForMonitor(monitor, 0, out uint dpi, out _) == 0 ? dpi / 96f : 1;
        try { _light = Microsoft.Win32.Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "SystemUsesLightTheme", 0) is int v && v != 0; }
        catch { _light = false; }
        _fadeFrom = Environment.TickCount64;
        Draw();
        SetTimer(_window, FadeTimer, 15, IntPtr.Zero);
    }

    float Opacity => (float)Math.Clamp((Environment.TickCount64 - _fadeFrom) / 120.0, 0, 1);

    void Fade()
    {
        _panel.Fade(Opacity);
        if (Opacity >= 1) KillTimer(_window, FadeTimer);
    }

    // The panel: a rounded plate with the icons in a row, a soft plate behind the one picked and
    // its name under it.
    void Draw()
    {
        var apps = _apps!;
        float s = _scale, pad = Pad * s, margin = Margin * s, band = Band * s;
        float icon = AppSwitch.IconSize(apps.Count, _monitor.Monitor.Width - 2 * (margin + 48 * s), Icon * s, pad);
        float cell = icon + 2 * pad;
        int w = (int)MathF.Ceiling(apps.Count * cell + 2 * margin), h = (int)MathF.Ceiling(cell + band + 1.5f * margin);
        var m = _monitor.Monitor;
        _panel.Draw(w, h, m.Left + (m.Width - w) / 2, m.Top + (m.Height - h) / 2, Opacity, g =>
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            using (var plate = Rounded(new RectangleF(0.5f, 0.5f, w - 1, h - 1), Radius * s))
            using (var fill = new SolidBrush(_light ? Color.FromArgb(230, 242, 242, 242) : Color.FromArgb(230, 34, 34, 34)))
            using (var edge = new Pen(_light ? Color.FromArgb(50, 0, 0, 0) : Color.FromArgb(60, 255, 255, 255), 1))
            {
                g.FillPath(fill, plate);
                g.DrawPath(edge, plate);
            }
            float x0 = margin + _at * cell;
            using (var pick = Rounded(new RectangleF(x0, margin, cell, cell), 14 * s))
            using (var soft = new SolidBrush(_light ? Color.FromArgb(32, 0, 0, 0) : Color.FromArgb(46, 255, 255, 255)))
                g.FillPath(soft, pick);
            for (int i = 0; i < apps.Count; i++)
                if (IconOf(apps[i]).Icon is { } bmp)
                    g.DrawImage(bmp, new RectangleF(margin + i * cell + pad, margin + pad, icon, icon));
            using var font = new Font("Segoe UI", 14 * s, FontStyle.Regular, GraphicsUnit.Pixel);
            using var text = new SolidBrush(_light ? Color.FromArgb(20, 20, 20) : Color.FromArgb(240, 240, 240));
            using var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center, Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap };
            string name = IconOf(apps[_at]).Name;
            float tw = Math.Min(g.MeasureString(name, font).Width + 4, w - 2 * margin);
            float tx = Math.Clamp(x0 + cell / 2 - tw / 2, margin, w - margin - tw);
            g.DrawString(name, font, text, new RectangleF(tx, margin + cell, tw, band), format);
        });
    }

    static GraphicsPath Rounded(RectangleF r, float radius)
    {
        float d = Math.Min(2 * radius, Math.Min(r.Width, r.Height));
        var p = new GraphicsPath();
        p.AddArc(r.Left, r.Top, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Top, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.Left, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    // ---------- icons ----------

    // An app's icon, as large as it comes, and its name; looked up once per app.
    (Bitmap? Icon, string Name) IconOf(SwitchApp app)
    {
        if (_icons.TryGetValue(app.Id, out var known)) return known;
        int size = (int)(Icon * Math.Max(_scale, PrimaryScale()));
        string name = AppWindows.AppName(app.Window);
        Bitmap? bmp = null;
        // The app as Start knows it: Store apps, and apps with an id of their own (Chrome, Telegram).
        if (AppIds.Of(app.Window) is { } id && Item(@"shell:AppsFolder\" + id) is { } item)
        {
            bmp = Image(item, size);
            if (bmp != null && Name(item) is { Length: > 0 } n) name = n;
        }
        // Its program's icon.
        if (bmp == null && ExePath(app.Window) is { } exe && Item(exe) is { } file) bmp = Image(file, size);
        // The window's own, small as it may be.
        bmp ??= WindowIcon(app.Window);
        return _icons[app.Id] = (bmp, name);
    }

    static float PrimaryScale() =>
        GetDpiForMonitor(MonitorFromPoint(default, MONITOR_DEFAULTTOPRIMARY), 0, out uint dpi, out _) == 0 ? dpi / 96f : 1;

    static IShellItem? Item(string path)
    {
        var iid = typeof(IShellItem).GUID;
        return SHCreateItemFromParsingName(path, IntPtr.Zero, ref iid, out var item) == 0 ? item : null;
    }

    static string? Name(IShellItem item)
    {
        if (item.GetDisplayName(0 /* SIGDN_NORMALDISPLAY */, out IntPtr p) != 0 || p == IntPtr.Zero) return null;
        string? s = Marshal.PtrToStringUni(p);
        Marshal.FreeCoTaskMem(p);
        return s;
    }

    static bool _told;

    static Bitmap? Image(IShellItem item, int size)
    {
        if (item is not IShellItemImageFactory f || f.GetImage(new SIZE { Cx = size, Cy = size }, 0x4 /* SIIGBF_ICONONLY */ | 0x1 /* SIIGBF_BIGGERSIZEOK */, out IntPtr hbm) != 0) return null;
        try
        {
            var bm = new BITMAP();
            if (GetObject(hbm, Marshal.SizeOf<BITMAP>(), ref bm) == 0 || bm.Width <= 0 || bm.Height <= 0) return null;
            int w = bm.Width, h = bm.Height;
            var bi = new BITMAPINFOHEADER { Size = Marshal.SizeOf<BITMAPINFOHEADER>(), Width = w, Height = -h, Planes = 1, BitCount = 32 };
            var px = new byte[w * h * 4];
            IntPtr dc = GetDC(IntPtr.Zero);
            int lines = GetDIBits(dc, hbm, 0, (uint)h, px, ref bi, 0);
            ReleaseDC(IntPtr.Zero, dc);
            if (lines != h) return null;
            // The shell hands its icons over premultiplied; a colour brighter than its alpha says
            // they are not, and no alpha at all that the picture is opaque.
            bool alpha = false, straight = false;
            for (int i = 0; i < px.Length; i += 4)
            {
                byte a = px[i + 3];
                alpha |= a != 0;
                straight |= px[i] > a || px[i + 1] > a || px[i + 2] > a;
            }
            if (!alpha) for (int i = 3; i < px.Length; i += 4) px[i] = 255;
            bool premultiplied = !alpha || !straight;
            if (!_told) { _told = true; Log.Write($"alt+tab: shell icons come {(premultiplied ? "premultiplied" : "with straight alpha")}, {w}x{h} for {size}"); }
            var bmp = new Bitmap(w, h, premultiplied ? PixelFormat.Format32bppPArgb : PixelFormat.Format32bppArgb);
            var data = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.WriteOnly, bmp.PixelFormat);
            for (int y = 0; y < h; y++) Marshal.Copy(px, y * w * 4, data.Scan0 + y * data.Stride, w * 4);
            bmp.UnlockBits(data);
            return bmp;
        }
        finally { DeleteObject(hbm); }
    }

    static string? ExePath(IntPtr hwnd)
    {
        GetWindowThreadProcessId(hwnd, out uint pid);
        IntPtr p = OpenProcess(0x1000 /* PROCESS_QUERY_LIMITED_INFORMATION */, false, pid);
        if (p == IntPtr.Zero) return null;
        try
        {
            var sb = new System.Text.StringBuilder(1024);
            int n = sb.Capacity;
            return QueryFullProcessImageName(p, 0, sb, ref n) ? sb.ToString() : null;
        }
        finally { CloseHandle(p); }
    }

    static Bitmap? WindowIcon(IntPtr hwnd)
    {
        SendMessageTimeout(hwnd, 0x007F /* WM_GETICON */, (IntPtr)1 /* ICON_BIG */, IntPtr.Zero, 2 /* SMTO_ABORTIFHUNG */, 100, out IntPtr h);
        if (h == IntPtr.Zero) h = GetClassLongPtr(hwnd, -14 /* GCLP_HICON */);
        if (h == IntPtr.Zero) return null;
        try
        {
            using var icon = System.Drawing.Icon.FromHandle(h);
            return icon.ToBitmap();
        }
        catch { return null; }
    }

    // ---------- the panel's window ----------

    // A see-through window over everything that clicks pass through, on every desktop and left
    // out of screen pictures; cloaked when unused, as the lane map is.
    sealed class Panel : NativeWindow
    {
        readonly AltTab _owner;
        public bool Shown { get; private set; }

        public Panel(AltTab owner)
        {
            _owner = owner;
            CreateHandle(new CreateParams
            {
                Caption = "Lanes switcher",
                Style = unchecked((int)WS_POPUP),
                ExStyle = (int)(WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_TOPMOST | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE),
                Width = 1,
                Height = 1,
            });
            SetWindowDisplayAffinity(Handle, WDA_EXCLUDEFROMCAPTURE);
            Cloak(true);
            SetWindowPos(Handle, HWND_TOPMOST, 0, 0, 1, 1, SWP_NOACTIVATE | SWP_SHOWWINDOW);
        }

        void Cloak(bool on)
        {
            int v = on ? 1 : 0;
            DwmSetWindowAttribute(Handle, 13 /* DWMWA_CLOAK */, ref v, 4);
        }

        // Draws w×h premultiplied pixels with `paint` and shows them at (x, y).
        public void Draw(int w, int h, int x, int y, float opacity, Action<Graphics> paint)
        {
            IntPtr screen = GetDC(IntPtr.Zero), mem = CreateCompatibleDC(screen);
            var bi = new BITMAPINFOHEADER { Size = Marshal.SizeOf<BITMAPINFOHEADER>(), Width = w, Height = -h, Planes = 1, BitCount = 32 };
            IntPtr dib = CreateDIBSection(mem, ref bi, 0, out IntPtr bits, IntPtr.Zero, 0);
            using (var bmp = new Bitmap(w, h, w * 4, PixelFormat.Format32bppPArgb, bits))
            using (var g = Graphics.FromImage(bmp))
            {
                g.Clear(Color.Transparent);
                paint(g);
            }
            IntPtr old = SelectObject(mem, dib);
            var at = new POINT { X = x, Y = y };
            var size = new SIZE { Cx = w, Cy = h };
            var from = new POINT();
            var blend = new BLENDFUNCTION { SourceConstantAlpha = (byte)Math.Clamp(opacity * 255, 0, 255), AlphaFormat = 1 /* AC_SRC_ALPHA */ };
            UpdateLayeredWindow(Handle, screen, ref at, ref size, mem, ref from, 0, ref blend, 2 /* ULW_ALPHA */);
            SelectObject(mem, old);
            DeleteObject(dib);
            DeleteDC(mem);
            ReleaseDC(IntPtr.Zero, screen);
            if (Shown) return;
            SetWindowPos(Handle, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
            Cloak(false);
            Shown = true;
        }

        public void Fade(float opacity)
        {
            var blend = new BLENDFUNCTION { SourceConstantAlpha = (byte)Math.Clamp(opacity * 255, 0, 255), AlphaFormat = 1 };
            UpdateLayeredWindow(Handle, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 0, ref blend, 2);
        }

        public void Hide()
        {
            if (!Shown) return;
            Cloak(true);
            Shown = false;
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg is >= Begin and <= Cancel or 0x0113) { _owner.OnMessage(m.Msg, (int)m.WParam); return; }
            base.WndProc(ref m);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    struct BITMAP { public int Type, Width, Height, WidthBytes; public ushort Planes, BitsPixel; public IntPtr Bits; }

    [ComImport, Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IShellItem
    {
        [PreserveSig] int BindToHandler(IntPtr bc, ref Guid bhid, ref Guid riid, out IntPtr ppv);
        [PreserveSig] int GetParent(out IShellItem parent);
        [PreserveSig] int GetDisplayName(uint sigdn, out IntPtr name);
        [PreserveSig] int GetAttributes(uint mask, out uint attributes);
        [PreserveSig] int Compare(IShellItem other, uint hint, out int order);
    }

    [ComImport, Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IShellItemImageFactory
    {
        [PreserveSig] int GetImage(SIZE size, int flags, out IntPtr hbitmap);
    }

    delegate void WinEventProc(IntPtr hook, uint ev, IntPtr hwnd, int obj, int child, uint thread, uint time);
    [DllImport("user32.dll")] static extern IntPtr SetWinEventHook(uint min, uint max, IntPtr module, WinEventProc proc, uint pid, uint thread, uint flags);
    [DllImport("user32.dll")] static extern UIntPtr SetTimer(IntPtr hwnd, nuint id, uint ms, IntPtr proc);
    [DllImport("user32.dll")] static extern bool KillTimer(IntPtr hwnd, nuint id);
    [DllImport("user32.dll")] static extern short GetAsyncKeyState(int vk);
    [DllImport("user32.dll")] static extern void keybd_event(byte vk, byte scan, uint flags, IntPtr extra);
    [DllImport("user32.dll")] static extern IntPtr GetClassLongPtr(IntPtr hwnd, int index);
    [DllImport("shcore.dll")] static extern int GetDpiForMonitor(IntPtr monitor, int type, out uint dpiX, out uint dpiY);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    static extern int SHCreateItemFromParsingName(string path, IntPtr bc, ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out IShellItem item);
    [DllImport("gdi32.dll")] static extern int GetObject(IntPtr obj, int size, ref BITMAP bitmap);
    [DllImport("gdi32.dll")] static extern int GetDIBits(IntPtr dc, IntPtr bitmap, uint start, uint lines, [Out] byte[] bits, ref BITMAPINFOHEADER info, uint usage);
    [DllImport("kernel32.dll")] static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr handle);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern bool QueryFullProcessImageName(IntPtr process, uint flags, System.Text.StringBuilder name, ref int size);
}
