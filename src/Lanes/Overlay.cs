using System.Runtime.InteropServices;
using static Lanes.Native;

namespace Lanes;

// The full-screen window Lanes draws its animations in. Topmost, never takes focus, shown on
// every virtual desktop (so a switch underneath does not hide it). Lanes only takes pictures of
// the screen while it is cloaked, so they are always of the desktop beneath it; screenshots you
// take while it is up show it.
sealed class Overlay : NativeWindow
{
    public event Action<int, int>? MouseMove;
    public event Action<int, int>? LeftDown;
    public event Action<int, int>? LeftUp;
    public event Action<int, int>? MiddleUp;
    public event Action<int, int>? RightUp;
    // Another program asked for the overview (an F3 key mapped to it, as on a Mac's keyboard):
    // it posts the registered message "Lanes.Overview" to this window, titled "Lanes".
    public event Action? OverviewAsked;
    static readonly int OverviewMessage = RegisterWindowMessage("Lanes.Overview");

    // The monitor the overlay is on, its work area (without the taskbar), and the part the
    // overlay covers: the whole monitor (Lanes shows the taskbar in it, see Engine.RaiseTaskbar).
    public RECT Monitor { get; private set; }
    public RECT Work { get; private set; }
    public RECT Area { get; private set; }
    public bool Shown { get; private set; }
    // A window that stays above the overlay while it is up (see ScreenPicture in Capture.cs).
    public IntPtr Under;

    public Overlay()
    {
        CreateHandle(new CreateParams
        {
            Caption = "Lanes",
            Style = unchecked((int)WS_POPUP),
            ExStyle = (int)(WS_EX_TOPMOST | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE | WS_EX_NOREDIRECTIONBITMAP),
            Width = 1,
            Height = 1,
        });
        // Lanes runs as administrator; programs that are not may still ask.
        ChangeWindowMessageFilterEx(Handle, OverviewMessage, 1 /* MSGFLT_ALLOW */, IntPtr.Zero);
        int round = 1; // DWMWCP_DONOTROUND: square corners on a full-screen window
        DwmSetWindowAttribute(Handle, DWMWA_WINDOW_CORNER_PREFERENCE, ref round, 4);
        // The window stays shown and is cloaked when not in use: a cloaked window takes no
        // clicks and is not drawn, but DWM keeps its last frame, so uncloaking shows the
        // picture at once. (Showing a hidden window takes a few frames to show its content.)
        Cloak(true);
        PickArea();
        SetWindowPos(Handle, HWND_TOPMOST, Area.Left, Area.Top, Area.Width, Area.Height, SWP_NOACTIVATE | SWP_SHOWWINDOW);
    }

    void Cloak(bool on)
    {
        int v = on ? 1 : 0;
        DwmSetWindowAttribute(Handle, 13 /* DWMWA_CLOAK */, ref v, 4);
    }

    // The monitor the pointer is on.
    public RECT PickArea()
    {
        var mi = PointerMonitor();
        Monitor = mi.Monitor;
        Work = mi.Work;
        Area = mi.Monitor;
        return Area;
    }

    // Under the kept window only while it is still among the topmost ones: its program may have
    // put it under ordinary windows since, and the overlay would go there with it, out of sight.
    IntPtr After => Under != IntPtr.Zero && AmongTopmost(Under) ? Under : HWND_TOPMOST;

    static bool AmongTopmost(IntPtr window)
    {
        for (IntPtr h = GetTopWindow(IntPtr.Zero); h != IntPtr.Zero; h = GetWindow(h, 2 /* GW_HWNDNEXT */))
        {
            if (h == window) return true;
            if (IsWindowVisible(h) && !Cloaked(h) && (GetWindowLongPtr(h, GWL_EXSTYLE).ToInt64() & WS_EX_TOPMOST) == 0) return false;
        }
        return false;
    }

    [DllImport("user32.dll")] static extern IntPtr GetTopWindow(IntPtr parent);
    [DllImport("user32.dll")] static extern IntPtr GetWindow(IntPtr hwnd, uint cmd);

    public float Scale => Math.Max(1, GetDpiForWindow(Handle)) / 96f;

    // Call after drawing the first frame for the new area.
    public void Show()
    {
        SetWindowPos(Handle, After, Area.Left, Area.Top, Area.Width, Area.Height, SWP_NOACTIVATE | SWP_SHOWWINDOW);
        Cloak(false);
        Shown = true;
    }

    public void Place() => SetWindowPos(Handle, After, Area.Left, Area.Top, Area.Width, Area.Height, SWP_NOACTIVATE);

    // Leaves `hole` (in the overlay's own coordinates) out of the window: what is under it shows
    // through and takes the clicks. Null fills it again.
    public void Hole(RECT? hole)
    {
        if (hole is not RECT h) { if (_holed) SetWindowRgn(Handle, IntPtr.Zero, true); _holed = false; return; }
        IntPtr all = CreateRectRgn(0, 0, Area.Width, Area.Height), cut = CreateRectRgn(h.Left, h.Top, h.Right, h.Bottom);
        CombineRgn(all, all, cut, 4 /* RGN_DIFF */);
        DeleteRegion(cut);
        // Windows owns the region from here on.
        SetWindowRgn(Handle, all, true);
        _holed = true;
    }

    bool _holed;

    [DllImport("gdi32.dll")] static extern IntPtr CreateRectRgn(int l, int t, int r, int b);
    [DllImport("gdi32.dll")] static extern int CombineRgn(IntPtr dest, IntPtr a, IntPtr b, int mode);
    [DllImport("gdi32.dll", EntryPoint = "DeleteObject")] static extern bool DeleteRegion(IntPtr obj);
    [DllImport("user32.dll")] static extern int SetWindowRgn(IntPtr hwnd, IntPtr rgn, bool redraw);

    public void Hide()
    {
        Cloak(true);
        Shown = false;
        Under = IntPtr.Zero;
    }

    // Leaves the overlay out of screen pictures for a moment, so Lanes can take one of what is
    // under it while it is up. False where Windows cannot (before Windows 10 2004).
    public bool LeaveOutOfPictures(bool on) =>
        SetWindowDisplayAffinity(Handle, on ? WDA_EXCLUDEFROMCAPTURE : WDA_NONE) && (!on || GetWindowDisplayAffinity(Handle, out uint a) && a == WDA_EXCLUDEFROMCAPTURE);

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == OverviewMessage)
        {
            OverviewAsked?.Invoke();
            return;
        }
        switch (m.Msg)
        {
            case WM_MOUSEACTIVATE:
                m.Result = MA_NOACTIVATE;
                return;
            case WM_SETCURSOR:
                Cursor.Current = Cursors.Default;
                m.Result = 1;
                return;
            case WM_MOUSEMOVE:
                MouseMove?.Invoke(X(m), Y(m));
                break;
            case WM_LBUTTONDOWN:
                // Keep getting the pointer while the button is down, for dragging.
                SetCapture(Handle);
                LeftDown?.Invoke(X(m), Y(m));
                break;
            case WM_LBUTTONUP:
                ReleaseCapture();
                LeftUp?.Invoke(X(m), Y(m));
                break;
            case WM_MBUTTONUP:
                MiddleUp?.Invoke(X(m), Y(m));
                break;
            case WM_RBUTTONUP:
                RightUp?.Invoke(X(m), Y(m));
                break;
        }
        base.WndProc(ref m);

        static int X(Message m) => (short)(m.LParam.ToInt64() & 0xFFFF);
        static int Y(Message m) => (short)((m.LParam.ToInt64() >> 16) & 0xFFFF);
    }
}

// The lane map after a switch: a small see-through window that stays over the desktop as long as
// Windows' own "Desktop N" pill does, then fades with it. Clicks go through it, and Lanes' screen
// pictures leave it out. Like the overlay, it stays shown on every desktop and is cloaked when unused.
sealed class MapWindow : NativeWindow
{
    public bool Shown { get; private set; }

    public MapWindow()
    {
        CreateHandle(new CreateParams
        {
            Caption = "Lanes map",
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

    // Shows these premultiplied pixels with their top-left corner at (x, y) on the screen.
    public void Show(Pixels p, int x, int y)
    {
        IntPtr screen = GetDC(IntPtr.Zero), mem = CreateCompatibleDC(screen);
        var bi = new BITMAPINFOHEADER { Size = Marshal.SizeOf<BITMAPINFOHEADER>(), Width = p.Width, Height = -p.Height, Planes = 1, BitCount = 32 };
        IntPtr dib = CreateDIBSection(mem, ref bi, 0, out IntPtr bits, IntPtr.Zero, 0);
        unsafe { Buffer.MemoryCopy((void*)p.Data, (void*)bits, (long)p.Width * p.Height * 4, (long)p.Width * p.Height * 4); }
        IntPtr old = SelectObject(mem, dib);
        var at = new POINT { X = x, Y = y };
        var size = new SIZE { Cx = p.Width, Cy = p.Height };
        var from = new POINT();
        var blend = new BLENDFUNCTION { SourceConstantAlpha = 255, AlphaFormat = 1 /* AC_SRC_ALPHA */ };
        UpdateLayeredWindow(Handle, screen, ref at, ref size, mem, ref from, 0, ref blend, 2 /* ULW_ALPHA */);
        SelectObject(mem, old);
        DeleteObject(dib);
        DeleteDC(mem);
        ReleaseDC(IntPtr.Zero, screen);
        SetWindowPos(Handle, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
        Cloak(false);
        Shown = true;
    }

    public void Move(int x, int y) => SetWindowPos(Handle, IntPtr.Zero, x, y, 0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);

    public void Raise() => SetWindowPos(Handle, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);

    public void Fade(float alpha)
    {
        var blend = new BLENDFUNCTION { SourceConstantAlpha = (byte)Math.Clamp(alpha * 255, 0, 255), AlphaFormat = 1 };
        UpdateLayeredWindow(Handle, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 0, ref blend, 2);
    }

    public void Hide()
    {
        if (!Shown) return;
        Cloak(true);
        Shown = false;
    }
}

// A live picture of another window, drawn by DWM inside the overlay.
sealed class Thumbnail : IDisposable
{
    readonly IntPtr _id;
    readonly RECT _source;
    public readonly IntPtr Window;
    public bool Valid => _id != IntPtr.Zero;

    public Thumbnail(IntPtr overlay, IntPtr window)
    {
        Window = window;
        if (DwmRegisterThumbnail(overlay, window, out _id) != 0) _id = IntPtr.Zero;
        // Leave out the invisible resize borders around the visible frame.
        GetWindowRect(window, out var wr);
        var fr = Frame(window);
        _source = new RECT(fr.Left - wr.Left, fr.Top - wr.Top, fr.Right - wr.Left, fr.Bottom - wr.Top);
    }

    // The part of `window` that is on the screen inside `area`.
    public Thumbnail(IntPtr overlay, IntPtr window, RECT area)
    {
        Window = window;
        if (DwmRegisterThumbnail(overlay, window, out _id) != 0) _id = IntPtr.Zero;
        GetWindowRect(window, out var wr);
        _source = new RECT(area.Left - wr.Left, area.Top - wr.Top, area.Right - wr.Left, area.Bottom - wr.Top);
    }

    // Draws the window into this box; the part above `clipTop` is left out, so whatever Lanes
    // draws there (the lane rows) looks as if it lies over the window.
    public void Place(float x, float y, float w, float h, float opacity = 1, float clipTop = float.NegativeInfinity)
    {
        if (!Valid) return;
        var src = _source;
        if (clipTop > y)
        {
            float cut = Math.Min(1, (clipTop - y) / Math.Max(1, h));
            src.Top += (int)MathF.Round(cut * _source.Height);
            h -= clipTop - y;
            y = clipTop;
            if (h <= 1 || src.Top >= src.Bottom) opacity = 0;
        }
        Set(x, y, w, h, src, opacity);
    }

    // Draws the window into this box, but only between `from` and `to` (along x when
    // `horizontal`, else along y): the rest lies under something Lanes draws as if above it.
    public void PlaceCut(float x, float y, float w, float h, float from, float to, bool horizontal, float opacity = 1)
    {
        if (!Valid) return;
        float a = horizontal ? x : y, len = Math.Max(1, horizontal ? w : h);
        float c0 = Math.Clamp(from, a, a + len), c1 = Math.Clamp(to, a, a + len);
        var src = _source;
        int n = horizontal ? _source.Right - _source.Left : _source.Bottom - _source.Top;
        int s0 = (int)MathF.Round((c0 - a) / len * n), s1 = (int)MathF.Round((c1 - a) / len * n);
        if (horizontal) { src.Left = _source.Left + s0; src.Right = _source.Left + s1; x = c0; w = c1 - c0; }
        else { src.Top = _source.Top + s0; src.Bottom = _source.Top + s1; y = c0; h = c1 - c0; }
        Set(x, y, w, h, src, c1 - c0 >= 0.5f && s1 > s0 ? opacity : 0);
    }

    void Set(float x, float y, float w, float h, RECT src, float opacity)
    {
        var p = new DWM_THUMBNAIL_PROPERTIES
        {
            Flags = DWM_TNP_RECTDESTINATION | DWM_TNP_RECTSOURCE | DWM_TNP_VISIBLE | DWM_TNP_OPACITY,
            Destination = new RECT((int)MathF.Round(x), (int)MathF.Round(y), (int)MathF.Round(x + w), (int)MathF.Round(y + Math.Max(0, h))),
            Source = src,
            Opacity = (byte)Math.Clamp(opacity * 255, 0, 255),
            Visible = opacity > 0.003 ? 1 : 0,
        };
        if (_holding) Pending[_id] = p;
        else DwmUpdateThumbnailProperties(_id, ref p);
    }

    public void Dispose()
    {
        if (!Valid) return;
        Pending.Remove(_id);
        DwmUnregisterThumbnail(_id);
    }

    // While Lanes draws a frame, changes to the live pictures wait for it to be drawn (see Gfx.End).
    static readonly Dictionary<IntPtr, DWM_THUMBNAIL_PROPERTIES> Pending = new();
    static bool _holding;

    public static void Hold() => _holding = true;

    public static void Release()
    {
        _holding = false;
        foreach (var (id, p) in Pending)
        {
            var q = p;
            DwmUpdateThumbnailProperties(id, ref q);
        }
        Pending.Clear();
    }
}

static class MessageLoop
{
    [StructLayout(LayoutKind.Sequential)]
    public struct MSG { public IntPtr Hwnd; public uint Message; public IntPtr WParam, LParam; public uint Time; public POINT Pt; public uint Private; }

    [DllImport("user32.dll")] public static extern int GetMessage(out MSG msg, IntPtr hwnd, uint min, uint max);
    [DllImport("user32.dll")] public static extern bool PeekMessage(out MSG msg, IntPtr hwnd, uint min, uint max, uint remove);
    [DllImport("user32.dll")] public static extern bool TranslateMessage(ref MSG msg);
    [DllImport("user32.dll")] public static extern IntPtr DispatchMessage(ref MSG msg);
    [DllImport("user32.dll")] public static extern uint MsgWaitForMultipleObjectsEx(uint count, IntPtr[]? handles, uint ms, uint wake, uint flags);

    // Handles every waiting message; false once WM_QUIT arrived.
    public static bool Pump()
    {
        while (PeekMessage(out var msg, IntPtr.Zero, 0, 0, 1))
        {
            if (msg.Message == 0x0012) return false;
            TranslateMessage(ref msg);
            DispatchMessage(ref msg);
        }
        return true;
    }

    // Sleeps until a message arrives or the timeout passes.
    public static void Wait(uint ms) => MsgWaitForMultipleObjectsEx(0, null, ms, 0x04FF /* QS_ALLINPUT */, 0x0004 /* MWMO_INPUTAVAILABLE */);
}
