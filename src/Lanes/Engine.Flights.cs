using System.Runtime.InteropServices;
using Lanes.Core;
using static Lanes.Native;

namespace Lanes;

// Windows minimizing and coming back, outside the overview, as in it: a window minimized any way
// (its own button, a taskbar click, Win+Down) flies into its app's taskbar button, and one Lanes
// brings back (a taskbar click, Alt+Tab) flies out of it to its place, the same way backwards.
// Windows' own animation is off meanwhile (WindowAnimation). The window's live picture flies in
// a see-through window of its own over the screen, so nothing else on it stops.
sealed partial class Engine
{
    sealed class Flight
    {
        public IntPtr Window;
        public Thumbnail Thumb = null!;
        // The whole window where it is (or comes back to) and in its taskbar button, on the screen.
        public Box Place, Button;
        public double Start;
        public bool Back;
        public double LandedAt = -1;
    }

    readonly List<Flight> _flights = new();
    FlightWindow _flightWindow = null!;
    IntPtr _minimizeHook;
    WinEventProc? _minimizeProc;

    void ListenForMinimize()
    {
        _minimizeProc = OnMinimized;
        _minimizeHook = SetWinEventHook(0x0016 /* EVENT_SYSTEM_MINIMIZESTART */, 0x0016, IntPtr.Zero, _minimizeProc, 0, 0, 0 /* WINEVENT_OUTOFCONTEXT */);
    }

    void OnMinimized(IntPtr hook, uint ev, IntPtr hwnd, int obj, int child, uint thread, uint time)
    {
        // The overview draws its own; a swipe has the screen.
        if (obj != 0 || !WindowAnimation.Taken || _overview != null || _switch != null) return;
        if (!AppWindows.IsAppKind(hwnd) || _flights.Any(f => f.Window == hwnd)) return;
        Fly(hwnd, back: false);
    }

    void Fly(IntPtr window, bool back)
    {
        var (whole, _) = AppWindows.Restored(window);
        _flightWindow.Show();
        var at = _flightWindow.Area;
        var place = new Box(whole.Left - at.Left, whole.Top - at.Top, whole.Width, whole.Height);
        var monitor = MonitorOf(MonitorFromPoint(new POINT { X = (whole.Left + whole.Right) / 2, Y = (whole.Top + whole.Bottom) / 2 }, MONITOR_DEFAULTTONEAREST)).Monitor;
        var (cx, cy, w) = TaskbarSpot(window, monitor);
        var f = new Flight
        {
            Window = window,
            Thumb = Thumbnail.Whole(_flightWindow.Handle, window),
            Place = place,
            Button = Minimize.Into(place, cx - at.Left, cy - at.Top, w),
            Start = Now,
            Back = back,
        };
        _flights.Add(f);
        StepFlights(Now);
        Log.Write(back ? "window coming back from the taskbar" : "window minimized into the taskbar");
    }

    void StepFlights(double now)
    {
        for (int i = _flights.Count - 1; i >= 0; i--)
        {
            var f = _flights[i];
            double u = Math.Clamp((now - f.Start) / Minimize.Time, 0, 1);
            var (b, alpha) = Minimize.At(f.Place, f.Button, f.Back ? 1 - u : u);
            f.Thumb.Place((float)b.X, (float)b.Y, (float)b.W, (float)b.H, (float)alpha);
            if (u < 1) continue;
            // Back in its place: the window itself shows there now, under its picture, which goes
            // once it has been drawn.
            if (f.Back && f.LandedAt < 0)
            {
                f.LandedAt = now;
                ShowWindow(f.Window, SW_RESTORE);
                AppWindows.Activate(f.Window);
                continue;
            }
            if (f.Back && now - f.LandedAt < 0.05) continue;
            f.Thumb.Dispose();
            _flights.RemoveAt(i);
        }
        if (_flights.Count == 0) _flightWindow.Hide();
    }

    // Where an app is on the taskbar, on the screen: the middle of its button and how wide its
    // icon is, as last read (else the middle of the taskbar on `monitor`).
    (double X, double Y, double W) TaskbarSpot(IntPtr window, RECT monitor)
    {
        if (Buttons?.Of(window) is { } b)
            return ((b.Rect.Left + b.Rect.Right) / 2.0, (b.Rect.Top + b.Rect.Bottom) / 2.0, b.Rect.Width * 0.6);
        if (TaskbarOn(monitor) is RECT bar)
            return ((bar.Left + bar.Right) / 2.0 + monitor.Left, (bar.Top + bar.Bottom) / 2.0 + monitor.Top, bar.Height * 0.6);
        return ((monitor.Left + monitor.Right) / 2.0, monitor.Bottom, 40 * _overlay.Scale);
    }

    // ---------- bringing a window ----------

    // Brings an app's window to the front (Alt+Tab, a taskbar button). A minimized one comes back
    // out of its taskbar button onto this desktop, wherever it was minimized: minimized, it had
    // left its desktop, as on a Mac. One on another desktop takes you there, as a swipe would.
    public void Bring(IntPtr window) => Post(() => BringNow(window));

    void BringNow(IntPtr window)
    {
        if (!IsWindow(window) || _switch != null) return;
        var desk = Desktops.WindowDesktop(window);
        if (IsIconic(window))
        {
            if (desk != Guid.Empty && desk != _current && !Desktops.IsPinned(window)) Desktops.MoveWindow(window, _current);
            if (WindowAnimation.Taken && _overview == null) Fly(window, back: true);
            else AppWindows.Activate(window);
            return;
        }
        if (desk != Guid.Empty && desk != _current && _overview == null && _lanes.Locate(desk) != null)
        {
            RefreshTitles();
            SwitchTo(desk);
            _current = desk;
            _lanes.Visited(desk);
            Settings.SaveLanes(_lanes);
            // Once Windows is there: on the thread that switched.
            Switches.Add(() => AppWindows.Activate(window));
            _pillX = _pillAside = null;
            LingerPill(p => DrawPill(p, desk, null, 0, 0, 1), Now);
            Log.Write($"brought a window on desktop {Desktops.IndexOf(desk) + 1}: went there");
            return;
        }
        AppWindows.Activate(window);
    }

    // An app's taskbar button clicked (see TaskbarButtons): its windows, from every desktop and
    // minimized ones too, spread over the screen, the lanes kept out of sight; with one window,
    // a minimized one comes back. Clicked again, it closes.
    public void AppClicked(AppRef app) => Post(() => ShowApp(app));

    void ShowApp(AppRef app)
    {
        if (!Ready || _switch != null) return;
        if (_overview is { App: { } shown } && shown == app)
        {
            CloseOverview();
            return;
        }
        if (_overview != null) CloseOverviewNow();
        var windows = AppWindows.OfApp(app);
        Log.Write($"taskbar: {app.Name} clicked, {windows.Count} windows");
        if (windows.Count == 1)
        {
            if (IsIconic(windows[0])) BringNow(windows[0]);
            else if (GetForegroundWindow() == windows[0]) PostMessage(windows[0], WM_SYSCOMMAND, (IntPtr)SC_MINIMIZE, IntPtr.Zero);
            else BringNow(windows[0]);
            return;
        }
        if (windows.Count == 0) return;
        HideThumbnails();
        _touchCaptureOf = _current;
        BeginOverview(app, windows);
        _overview!.Tracking = false;
        SettleOverview(1, 0);
    }

    // Windows' own row of thumbnails, up over the button the pointer rests on, goes: the app's
    // windows are about to be shown large. (Explorer shows it again when the pointer moves on.)
    static void HideThumbnails()
    {
        for (IntPtr h = IntPtr.Zero; (h = FindWindowEx(IntPtr.Zero, h, "TaskListThumbnailWnd", null)) != IntPtr.Zero;)
            if (IsWindowVisible(h))
            {
                ShowWindow(h, SW_HIDE);
                Log.Write("taskbar: Windows' thumbnails put away");
            }
    }

    delegate void WinEventProc(IntPtr hook, uint ev, IntPtr hwnd, int obj, int child, uint thread, uint time);
    [DllImport("user32.dll")] static extern IntPtr SetWinEventHook(uint min, uint max, IntPtr module, WinEventProc proc, uint pid, uint thread, uint flags);
}

// A see-through window over all the screens that windows fly in, to and from the taskbar: it has
// nothing of its own to show (no surface at all), only their live pictures, and clicks pass
// through it. Shown on every desktop, cloaked when unused.
sealed class FlightWindow : NativeWindow
{
    public RECT Area { get; private set; }

    public FlightWindow(bool inScreenshots)
    {
        CreateHandle(new CreateParams
        {
            Caption = "Lanes flights",
            Style = unchecked((int)WS_POPUP),
            ExStyle = (int)(WS_EX_NOREDIRECTIONBITMAP | WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_TOPMOST | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE),
            Width = 1,
            Height = 1,
        });
        SetLayeredWindowAttributes(Handle, 0, 255, 2 /* LWA_ALPHA */);
        if (!inScreenshots) SetWindowDisplayAffinity(Handle, WDA_EXCLUDEFROMCAPTURE);
        Cloak(true);
        SetWindowPos(Handle, HWND_TOPMOST, 0, 0, 1, 1, SWP_NOACTIVATE | SWP_SHOWWINDOW);
    }

    void Cloak(bool on)
    {
        int v = on ? 1 : 0;
        DwmSetWindowAttribute(Handle, 13 /* DWMWA_CLOAK */, ref v, 4);
    }

    // Over every screen as they are now, in front of the other topmost windows (the taskbar).
    public void Show()
    {
        Area = new RECT(GetSystemMetrics(76), GetSystemMetrics(77), GetSystemMetrics(76) + GetSystemMetrics(78), GetSystemMetrics(77) + GetSystemMetrics(79));
        SetWindowPos(Handle, HWND_TOPMOST, Area.Left, Area.Top, Area.Width, Area.Height, SWP_NOACTIVATE | SWP_SHOWWINDOW);
        Cloak(false);
    }

    public void Hide() => Cloak(true);

    [DllImport("user32.dll")] static extern bool SetLayeredWindowAttributes(IntPtr hwnd, uint key, byte alpha, uint flags);
    [DllImport("user32.dll")] static extern int GetSystemMetrics(int index);
}
