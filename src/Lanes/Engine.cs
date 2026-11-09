using System.Collections.Concurrent;
using System.Diagnostics;
using Lanes.Core;
using Vortice.Direct2D1;
using Vortice.DirectWrite;
using static Lanes.Native;

namespace Lanes;

// Everything that runs while you swipe lives on one thread: the touchpad reports arrive here,
// the gesture is recognised here, and each frame is drawn here right after the freshest report.
sealed partial class Engine : IGestureSink
{
    const int WM_COMMAND_QUEUED = WM_USER + 1, WM_DESKTOP_CHANGED = WM_USER + 2;

    readonly Settings _settings;
    readonly Tuning _tuning = new();
    readonly bool _demo;
    readonly ConcurrentQueue<Action> _commands = new();
    readonly Stopwatch _clock = Stopwatch.StartNew();
    Thread? _thread;
    MessageWindow _msg = null!;
    Overlay _overlay = null!;
    MapWindow _map = null!;
    Gfx _gfx = null!;
    Touchpad _touchpad = null!;
    readonly Dictionary<IntPtr, GestureRecognizer> _recognizers = new();
    IntPtr _activeDevice;
    LaneMap _lanes = new();
    Guid _current;

    // Pictures of each desktop as it looked when last seen, on the CPU and on the GPU.
    readonly Dictionary<Guid, Pixels> _snapshots = new();
    readonly Dictionary<Guid, ID2D1Bitmap1> _bitmaps = new();
    readonly Dictionary<Guid, ID2D1Bitmap1> _tilePictures = new();
    readonly Dictionary<Guid, string> _appOf = new();
    readonly Dictionary<Guid, (Pixels Sharp, Pixels Blurred, string? Path)> _wallpaperPixels = new();
    readonly Dictionary<Guid, (ID2D1Bitmap1 Sharp, ID2D1Bitmap1 Blurred)> _wallpapers = new();
    // Whether a window covered the whole screen (the taskbar hidden) when the picture was taken.
    readonly Dictionary<Guid, bool> _fullScreen = new();
    // Each desktop itself, wallpaper and icons, as the overview shows it under the windows.
    readonly Dictionary<Guid, (Pixels Pixels, long At)> _deskPixels = new();
    readonly Dictionary<Guid, ID2D1Bitmap1> _deskBitmaps = new();
    Task? _deskTask;
    // The overlay area the pictures on the GPU are cut for.
    RECT _picturesFor;
    Task<(Pixels Pixels, IntPtr KeepUnder)>? _touchCapture;
    Guid _touchCaptureOf;
    // Fingers on each touchpad in its last report (a laptop's own pad keeps reporting none).
    readonly Dictionary<IntPtr, int> _fingersOn = new();
    bool Touching => _fingersOn.Values.Any(n => n > 0);
    readonly Dictionary<IntPtr, long> _lastReport = new();
    readonly Dictionary<Guid, string> _titles = new(), _names = new();
    long _lastSnapshot;

    SwitchState? _switch;
    OverviewState? _overview;
    bool _ignoring; // a swipe that Lanes leaves alone until the fingers lift

    public bool Ready { get; private set; }
    // When the engine thread last went round its loop; the tray's watchdog reads it.
    public long Heartbeat;
    public IntPtr OverlayHandle => _overlay?.Handle ?? IntPtr.Zero;
    public event Action<string>? Status;

    public Engine(Settings settings, bool demo)
    {
        _settings = settings;
        _demo = demo;
    }

    public void Start()
    {
        var started = new ManualResetEventSlim();
        _thread = new Thread(() => Run(started)) { IsBackground = true, Name = "engine" };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        started.Wait(10000);
    }

    public void Post(Action action)
    {
        _commands.Enqueue(action);
        if (_msg != null) PostMessage(_msg.Handle, WM_COMMAND_QUEUED, IntPtr.Zero, IntPtr.Zero);
    }

    public void Stop() => Post(() => _running = false);

    bool _running = true;

    double Now => _clock.Elapsed.TotalSeconds;

    void Run(ManualResetEventSlim started)
    {
        try
        {
            _msg = new MessageWindow(this);
            _overlay = new Overlay();
            _map = new MapWindow();
            _overlay.PickArea();
            _picturesFor = _overlay.Area;
            _gfx = new Gfx();
            _gfx.Attach(_overlay.Handle, _overlay.Area.Width, _overlay.Area.Height);
            _overlay.MouseMove += OnMouseMove;
            _overlay.LeftDown += OnLeftDown;
            _overlay.LeftUp += OnLeftUp;
            _overlay.MiddleUp += OnMiddleUp;
            _overlay.RightUp += OnRightUp;
            _overlay.OverviewAsked += ToggleOverview;
            _touchpad = new Touchpad(OnFrame);
            _touchpad.Register(_msg.Handle);
            _awake = new Awake();
            if (Desktops.Load())
            {
                Desktops.Pin(_overlay.Handle);
                Desktops.Pin(_map.Handle);
                WindowsPill.Repair();
                VolumeWatch.Changed += () => Post(OnVolumeChanged);
                VolumeWatch.Watch();
                Desktops.Listen(_msg.Handle, WM_DESKTOP_CHANGED);
                SetUpLanes();
                Ready = true;
            }
            else Status?.Invoke("This Windows version is not supported");
            PrepareWallpapers();
            ComposeMissing();
        }
        catch (Exception e)
        {
            Log.Write($"engine start: {e}");
            Status?.Invoke("Lanes could not start: see the log");
        }
        finally { started.Set(); }

        while (_running)
        {
            Interlocked.Exchange(ref Heartbeat, Environment.TickCount64);
            try
            {
                if (Animating)
                {
                    if (!MessageLoop.Pump()) break;
                    _gfx.WaitForFrame();
                    if (!MessageLoop.Pump()) break;
                    Tick();
                    if (Animating || _overlay.Shown) Render();
                    // On the CI runner, which draws in software, frames otherwise queue up ahead of
                    // the screen while DWM moves the live window pictures at once, so recordings
                    // show the two apart. Real graphics hardware keeps them in step.
                    if (_demo) DwmFlush();
                }
                else
                {
                    // Faster while Lanes' pill is fading after a switch.
                    MessageLoop.Wait(_map.Shown || _revealAt > 0 || WindowsPill.Hidden ? 15u : 200u);
                    if (!MessageLoop.Pump()) break;
                    Tick();
                    Idle();
                }
            }
            catch (Exception e)
            {
                Log.Write($"engine: {e}");
                Reset();
            }
        }
        _awake?.Dispose();
        _overlay.Hide();
        _map.Hide();
        WindowsPill.Restore();
    }

    bool Animating => _switch != null || _overview is { } o && (o.Tracking || o.Spring != null || o.Zoom != null || o.Swipe != null || o.Moving || o.Added != null || o.Scroll != null || o.Dwell != null) || _redraw;
    Awake? _awake;
    bool _redraw;

    void Tick()
    {
        long ms = Environment.TickCount64;
        foreach (var r in _recognizers.Values) r.OnTick(ms);
        // A touchpad that went quiet (lifted fingers without a last report, or disconnected)
        // no longer holds the swipe or counts as touched.
        foreach (var (device, last) in _lastReport)
            if (ms - last > GestureRecognizer.SilenceMs && _fingersOn.GetValueOrDefault(device) > 0)
            {
                _fingersOn[device] = 0;
                if (_activeDevice == device) _activeDevice = IntPtr.Zero;
            }
        double now = Now;
        if (_switch != null) StepSwitch(now);
        if (_overview != null) StepOverview(now);
        if (_retakeAt > 0 && now >= _retakeAt) Retake();
        if (_map.Shown && _switch == null) StepLingeringPill(now);
        // Explorer's pop-up window goes back once Windows' pill would be gone (it stays 2 to 2.5
        // seconds), or for the volume a moment after it changed (Explorer has put it in by then).
        WindowsPill.Watch();
        if (WindowsPill.Hidden && _switch == null && now - _lastSwitchAt > 3) WindowsPill.Restore();
        if (_revealAt > 0 && now >= _revealAt)
        {
            _revealAt = 0;
            WindowsPill.Reveal();
        }
    }

    // When Lanes itself changed what is on this desktop while the overview is up (a window moved
    // away or closed, a desktop closed into this one, a switch inside it), the picture it keeps
    // of the desktop is taken again a moment later, from under the overlay; otherwise a later
    // swipe would show the desktop as it was.
    double _retakeAt;

    // The volume changed while Windows' pill is kept away: its window comes back, showing the
    // volume, unless Windows has not switched yet (its pill would then come up in it).
    double _revealAt;

    void OnVolumeChanged()
    {
        if (WindowsPill.Hidden && !(_switch is { Switched: false }) && Now - _lastSwitchAt > 0.15) _revealAt = Now + 0.06;
    }

    void RetakeSoon() => _retakeAt = Now + 0.3;

    void Retake()
    {
        _retakeAt = 0;
        if (_overview == null || _switch != null) return;
        if (!_overlay.LeaveOutOfPictures(true))
        {
            _overlay.LeaveOutOfPictures(false);
            return;
        }
        var desktop = _current;
        var monitor = _overlay.Monitor;
        var own = _overlay.Handle;
        Task.Run(() =>
        {
            DwmFlush();
            return Capture.ScreenWithout(monitor, own, out _);
        }).ContinueWith(t => Post(() =>
        {
            _overlay.LeaveOutOfPictures(false);
            if (t.Status != TaskStatus.RanToCompletion) return;
            if (desktop != _current || _overview == null) { t.Result.Dispose(); return; }
            Remember(desktop, t.Result);
            if (_overview.Strip.Tiles.FirstOrDefault(x => x.Desktop == desktop) is { } tile) TilePicture(desktop, (float)tile.Rect.W, (float)tile.Rect.H);
            _redraw = true;
            Log.Write("picture of this desktop taken again");
        }));
    }

    void Render()
    {
        _redraw = false;
        if (_switch != null) RenderSwitch();
        else if (_overview != null) RenderOverview();
    }

    // Abandon whatever was on screen after an error: the desktop stays usable.
    void Reset()
    {
        try
        {
            _switch = null;
            CloseOverviewNow();
            _overlay.Hide();
            _map.Hide();
            _ignoring = false;
        }
        catch { }
    }

    // ---------- desktops and lanes ----------

    void SetUpLanes()
    {
        var saved = Settings.LoadLanes();
        _current = Desktops.Current;
        _lanes = saved ?? LaneMap.Fresh(Desktops.All(), _current);
        if (saved != null) _lanes.Reconcile(Desktops.All(), _current);
        else
        {
            // First start: everything you had becomes the Main lane, with a new desktop above and below.
            foreach (int lane in _lanes.EmptyLanes().ToList())
            {
                var made = Desktops.Create();
                if (made != Guid.Empty) { _lanes.Add(made, lane); _lanes.Made.Add(made); }
            }
            SyncOrder();
            _lanes.Reconcile(Desktops.All(), Desktops.Current);
            Log.Write("first start: lanes made");
        }
        Settings.SaveLanes(_lanes);
        Log.Write("lanes: " + string.Join(" | ", _lanes.Lanes.Select(l => l.Desktops.Count)) + " desktops");
    }

    // Keeps Windows' own order of desktops (Task View, Ctrl+Win+arrows) the same as the lanes read left to right, top to bottom.
    void SyncOrder()
    {
        int i = 0;
        foreach (var lane in _lanes.Lanes)
            foreach (var d in lane.Desktops)
                Desktops.Move(d, i++);
    }

    void OnDesktopChanged()
    {
        if (!Ready) return;
        var now = Desktops.Current;
        // Lanes' own switches set _current first; any other (a taskbar button, Ctrl+Win+arrow)
        // leaves the open overview showing another desktop's windows: it closes.
        if (now != _current && _overview != null && _switch == null) CloseOverviewNow();
        _current = now;
        _lastSwitchAt = Now;
        _lanes.Reconcile(Desktops.All(), _current);
        Settings.SaveLanes(_lanes);
        // A fresh picture in a moment, once the desktop has drawn and Windows' "Desktop N" pill is gone.
        _lastSnapshot = Environment.TickCount64 - 2500;
        PrepareWallpapers();
    }

    // Names for the pill and the overview, worked out once per swipe: a name you gave the
    // desktop, else the app in front on it when it was last seen, else "Desktop N".
    void RefreshTitles()
    {
        _titles.Clear();
        _names.Clear();
        var all = Desktops.All();
        for (int i = 0; i < all.Count; i++)
        {
            string name = Desktops.Name(all[i]);
            _names[all[i]] = name.Length > 0 ? name : $"Desktop {i + 1}";
            if (name.Length == 0 && _appOf.TryGetValue(all[i], out var app)) name = app;
            _titles[all[i]] = name.Length > 0 ? name : $"Desktop {i + 1}";
        }
    }

    string Title(Guid desktop) => _titles.TryGetValue(desktop, out var t) ? t : "Desktop";

    // The name Windows itself shows for a desktop: the one you gave it, or "Desktop N".
    string WindowsName(Guid desktop) => _names.TryGetValue(desktop, out var t) ? t : $"Desktop {Desktops.IndexOf(desktop) + 1}";

    // ---------- pictures ----------

    void Idle()
    {
        if (!Ready || _overlay.Shown || _switch != null || _overview != null || Touching) return;
        long now = Environment.TickCount64;
        if (now - _lastWindowCheck >= 1000)
        {
            _lastWindowCheck = now;
            CheckOtherDesktops();
        }
        if (now - _lastSnapshot < 5000) return;
        _lastSnapshot = now;
        _awake?.Renew();
        // As at touch time, windows that leave themselves out of Lanes' pictures (Ciao) do.
        Remember(_current, Capture.ScreenWithout(PointerMonitor().Monitor, _overlay.Handle, out _));
        Warm();
        TakeDesk();
    }

    // The desktop under the windows (wallpaper and icons), taken now and then on the thread pool:
    // icons change seldom, and the overview needs it at once.
    void TakeDesk()
    {
        if (_deskTask is { IsCompleted: false }) return;
        if (_deskPixels.TryGetValue(_current, out var had) && Environment.TickCount64 - had.At < 30000) return;
        var desktop = _current;
        var monitor = PointerMonitor().Monitor;
        _deskTask = Task.Run(() => Capture.Desktop(monitor)).ContinueWith(t => Post(() =>
        {
            if (t.Status != TaskStatus.RanToCompletion || t.Result is not { } p) return;
            // Taken while Windows was switching: it may show the other desktop's wallpaper.
            if (desktop != _current || Now - _lastSwitchAt < 1) { p.Dispose(); return; }
            if (_deskPixels.Remove(desktop, out var old)) old.Pixels.Dispose();
            _deskPixels[desktop] = (p, Environment.TickCount64);
            if (_deskBitmaps.Remove(desktop, out var b)) b.Dispose();
        }));
    }

    ID2D1Bitmap1? DeskPicture(Guid desktop)
    {
        if (_deskBitmaps.TryGetValue(desktop, out var b)) return b;
        if (_deskPixels.TryGetValue(desktop, out var d) && d.Pixels.Width == _overlay.Monitor.Width && d.Pixels.Height == _overlay.Monitor.Height)
            return _deskBitmaps[desktop] = _gfx.Bitmap(d.Pixels, Inner);
        return Wallpaper(desktop)?.Sharp;
    }

    // ---------- windows moved to other desktops ----------

    // The windows on each other desktop when Lanes last looked. One moved there since (or one
    // that left or closed) would otherwise be missing from its picture, and come in late, after
    // the swipe: the picture gets the new window drawn in, or is made again from the windows.
    readonly Dictionary<Guid, List<AppWindow>> _windowsSeen = new();
    readonly HashSet<Guid> _redrawing = new();
    long _lastWindowCheck;

    void CheckOtherDesktops()
    {
        var monitor = PointerMonitor().Monitor;
        var all = AppWindows.OnOtherDesktops(monitor, _overlay.Handle);
        // This desktop's picture is taken live; it is looked at again once you have left it.
        _windowsSeen.Remove(_current);
        foreach (var d in Desktops.All())
        {
            if (d == _current || _redrawing.Contains(d)) continue;
            var now = all.GetValueOrDefault(d) ?? new List<AppWindow>();
            if (!_windowsSeen.TryGetValue(d, out var seen))
            {
                _windowsSeen[d] = now;
                continue;
            }
            static bool Same(AppWindow a, AppWindow b) => a.Handle == b.Handle && a.Frame.Left == b.Frame.Left && a.Frame.Top == b.Frame.Top
                && a.Frame.Right == b.Frame.Right && a.Frame.Bottom == b.Frame.Bottom;
            if (seen.Count == now.Count && seen.Zip(now).All(x => Same(x.First, x.Second))) continue;
            _windowsSeen[d] = now;
            var came = now.Where(w => !seen.Any(x => Same(x, w))).ToList();
            bool onlyCame = seen.All(x => now.Any(w => Same(x, w)));
            Redraw(d, onlyCame ? came : null, now, monitor);
        }
    }

    // A new picture of desktop `d` on the thread pool: the one it has with the windows that came
    // drawn over it, or (`came` null) its wallpaper with all its windows.
    void Redraw(Guid d, List<AppWindow>? came, List<AppWindow> windows, RECT monitor)
    {
        _snapshots.TryGetValue(d, out var had);
        var start = came != null && had != null && had.Width == monitor.Width && had.Height == monitor.Height ? had.Copy() : null;
        var front = windows.Count > 0 ? windows[^1] : null;
        bool full = front != null && front.Frame.Left <= monitor.Left && front.Frame.Top <= monitor.Top
            && front.Frame.Right >= monitor.Right && front.Frame.Bottom >= monitor.Bottom;
        _redrawing.Add(d);
        Task.Run(() =>
        {
            Pixels picture;
            if (start != null)
            {
                picture = start;
                Capture.DrawWindows(picture, came!, monitor);
            }
            else picture = Capture.Composed(WallpaperPixels(d, monitor), windows, monitor);
            return picture;
        }).ContinueWith(t => Post(() =>
        {
            _redrawing.Remove(d);
            if (t.Status != TaskStatus.RanToCompletion) { start?.Dispose(); return; }
            var p = t.Result;
            // You went there meanwhile: the picture taken then is newer.
            if (d == _current || _snapshots.GetValueOrDefault(d) != had) { p.Dispose(); return; }
            had?.Dispose();
            _snapshots[d] = p;
            if (_bitmaps.Remove(d, out var bmp)) bmp.Dispose();
            if (_tilePictures.Remove(d, out var tile)) tile.Dispose();
            _fullScreen[d] = full;
            if (front != null) _appOf[d] = front.App; else _appOf.Remove(d);
            Log.Write($"picture of desktop {Desktops.IndexOf(d) + 1} {(start != null ? $"took in {string.Join(", ", came!.Select(w => w.App))}" : "made again from its windows")}");
        }));
    }

    // The wallpaper of a desktop on this monitor, made now if Lanes has none of that size yet.
    Pixels WallpaperPixels(Guid d, RECT monitor)
    {
        (Pixels Sharp, Pixels Blurred, string? Path) wall;
        bool have;
        lock (_wallpaperPixels) have = _wallpaperPixels.TryGetValue(d, out wall);
        if (!have || wall.Sharp.Width != monitor.Width || wall.Sharp.Height != monitor.Height) wall = MakeWallpaper(d, monitor);
        return wall.Sharp;
    }

    // ---------- the taskbar in pictures ----------

    // A picture shows the wallpaper where the taskbar is: in a swipe the one taskbar is the live
    // one, which stays put over all desktops, with the desktops sliding under it.

    // Where the taskbar is in a picture of the whole monitor.
    static RECT? TaskbarOn(RECT monitor)
    {
        foreach (var cls in new[] { "Shell_TrayWnd", "Shell_SecondaryTrayWnd" })
            for (IntPtr h = IntPtr.Zero; (h = FindWindowEx(IntPtr.Zero, h, cls, null)) != IntPtr.Zero;)
            {
                if (!IsWindowVisible(h)) continue;
                var f = Frame(h);
                int l = Math.Max(f.Left, monitor.Left), t = Math.Max(f.Top, monitor.Top), r = Math.Min(f.Right, monitor.Right), b = Math.Min(f.Bottom, monitor.Bottom);
                if (r > l && b > t) return new RECT(l - monitor.Left, t - monitor.Top, r - monitor.Left, b - monitor.Top);
            }
        return null;
    }

    // Puts the pictures a swipe from here may need on the GPU ahead of time, so the first frame
    // of a swipe does not wait for uploads.
    void Warm()
    {
        Picture(_current);
        foreach (var d in new[] { Direction.Left, Direction.Right, Direction.Up, Direction.Down })
            if (_lanes.Neighbor(_current, d) is Guid n) Picture(n);
    }

    void Remember(Guid desktop, Pixels pixels)
    {
        if (_snapshots.TryGetValue(desktop, out var old) && old != pixels)
        {
            CoverPill(pixels, old);
            old.Dispose();
        }
        _snapshots[desktop] = pixels;
        if (_bitmaps.Remove(desktop, out var bmp)) bmp.Dispose();
        if (_tilePictures.Remove(desktop, out var tile)) tile.Dispose();
        // The app in front names the desktop in the overview; with only the desktop itself or the
        // taskbar in front it goes by its number.
        var fg = GetForegroundWindow();
        if (fg == IntPtr.Zero || ClassName(fg) is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd" or "XamlExplorerHostIslandWindow")
            _appOf.Remove(desktop);
        else if (fg != _overlay.Handle && Desktops.OnCurrent(fg))
            _appOf[desktop] = ClassName(fg) == "CabinetWClass" ? "File Explorer" : AppWindows.AppName(fg);
        _fullScreen[desktop] = FullScreenInFront(pixels.Width, pixels.Height);
        var monitor = PointerMonitor().Monitor;
        if (!_fullScreen[desktop] && monitor.Width == pixels.Width && monitor.Height == pixels.Height && TaskbarOn(monitor) is RECT bar)
        {
            // Under the taskbar, what is behind it: the wallpaper (else an empty bar of its colour).
            Pixels? wall = null;
            lock (_wallpaperPixels)
                if (_wallpaperPixels.TryGetValue(desktop, out var w) && w.Sharp.Width == pixels.Width && w.Sharp.Height == pixels.Height) wall = w.Sharp;
            if (wall != null) pixels.Paste(wall, bar);
            else pixels.Fill(bar, pixels.Plain(bar));
        }
    }

    // A picture taken within two seconds of a switch can have Windows' "Desktop N" pill in it
    // (after a switch with the keyboard, or when Lanes could not keep it away), and the copy
    // would ride on the desktop through the next swipe. Its place is filled from the picture
    // this desktop had before, blended in at the edges.
    void CoverPill(Pixels fresh, Pixels old)
    {
        if (Now - _lastSwitchAt > 2.3 || old.Width != fresh.Width || old.Height != fresh.Height) return;
        var mi = PointerMonitor();
        if (mi.Monitor.Width != fresh.Width || mi.Monitor.Height != fresh.Height) return;
        float s = _overlay.Scale, edge = 10 * s;
        var font = _gfx.Font(14 * s, FontWeight.Normal, centred: true, family: "Segoe UI Variable Text");
        float pw = Math.Max(_gfx.TextWidth(WindowsName(_current), font) + 69 * s, 160 * s), ph = 48 * s;
        float cx = (mi.Work.Left + mi.Work.Right) / 2f - mi.Monitor.Left, bottom = mi.Work.Bottom - mi.Monitor.Top - 13 * s;
        int l = Math.Max(0, (int)(cx - pw / 2 - edge)), r = Math.Min(fresh.Width, (int)(cx + pw / 2 + edge));
        int t = Math.Max(0, (int)(bottom - ph - edge)), b = Math.Min(fresh.Height, (int)(bottom + edge));
        unsafe
        {
            byte* to = (byte*)fresh.Data, from = (byte*)old.Data;
            for (int y = t; y < b; y++)
                for (int x = l; x < r; x++)
                {
                    float d = Math.Min(Math.Min(x - l, r - 1 - x), Math.Min(y - t, b - 1 - y));
                    float k = Math.Clamp(d / edge, 0, 1);
                    long i = ((long)y * fresh.Width + x) * 4;
                    for (int c = 0; c < 4; c++) to[i + c] = (byte)(to[i + c] + (from[i + c] - to[i + c]) * k);
                }
        }
    }

    // A window in front that covers the whole monitor (a video, a game, a browser in full
    // screen): Windows hides the taskbar behind it.
    bool FullScreenInFront(int width, int height)
    {
        var fg = GetForegroundWindow();
        if (fg == IntPtr.Zero || fg == _overlay.Handle || ClassName(fg) is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd") return false;
        var m = MonitorOf(MonitorFromWindow(fg, MONITOR_DEFAULTTONEAREST)).Monitor;
        if (m.Width != width || m.Height != height) return false;
        var f = Frame(fg);
        return f.Left <= m.Left && f.Top <= m.Top && f.Right >= m.Right && f.Bottom >= m.Bottom;
    }

    bool FullScreen(Guid? desktop) => desktop is Guid d && _fullScreen.GetValueOrDefault(d);

    // The overlay covers the monitor the pointer is on. Call while the overlay is hidden.
    void UseArea()
    {
        var area = _overlay.PickArea();
        if (area.Left == _picturesFor.Left && area.Top == _picturesFor.Top && area.Right == _picturesFor.Right && area.Bottom == _picturesFor.Bottom
            && _gfx.Width == area.Width && _gfx.Height == area.Height) return;
        foreach (var b in _bitmaps.Values) b.Dispose();
        foreach (var b in _tilePictures.Values) b.Dispose();
        foreach (var (a, b) in _wallpapers.Values) { a.Dispose(); b.Dispose(); }
        foreach (var b in _deskBitmaps.Values) b.Dispose();
        _bitmaps.Clear(); _tilePictures.Clear(); _wallpapers.Clear(); _deskBitmaps.Clear();
        _picturesFor = area;
        _gfx.Attach(_overlay.Handle, area.Width, area.Height);
    }

    // The taskbar, as a live picture where it is: it stays put while desktops move under it, as
    // the real one would. Lanes covers the whole monitor so that nothing else shows in the
    // taskbar's place (a full-screen video behind the window in front). Registered after the
    // windows' pictures, so it is drawn over them; call again after new ones.
    Thumbnail? _taskbar;

    void RaiseTaskbar()
    {
        DropTaskbar();
        var m = _overlay.Monitor;
        foreach (var cls in new[] { "Shell_TrayWnd", "Shell_SecondaryTrayWnd" })
            for (IntPtr h = IntPtr.Zero; (h = FindWindowEx(IntPtr.Zero, h, cls, null)) != IntPtr.Zero;)
            {
                var f = Frame(h);
                if (!IsWindowVisible(h) || f.Right <= m.Left || f.Left >= m.Right || f.Bottom <= m.Top || f.Top >= m.Bottom) continue;
                _taskbar = new Thumbnail(_overlay.Handle, h);
                for (int i = 0; i < Shade.Length; i++) _taskbarShade.Add(new Thumbnail(_overlay.Handle, h));
                return;
            }
    }

    void DropTaskbar()
    {
        _taskbar?.Dispose();
        _taskbar = null;
        foreach (var t in _taskbarShade) t.Dispose();
        _taskbarShade.Clear();
    }

    // Thin slices of the taskbar next to a full-screen window lying over it, each over black and
    // a little less faded than the last: the window's shadow on the taskbar.
    readonly List<Thumbnail> _taskbarShade = new();
    static readonly float[] Shade = { 0.42f, 0.56f, 0.68f, 0.79f, 0.88f, 0.95f };

    void PlaceTaskbar(float opacity)
    {
        if (_taskbar is not { } t) return;
        var f = Frame(t.Window);
        t.Place(f.Left - _overlay.Area.Left, f.Top - _overlay.Area.Top, f.Width, f.Height, opacity);
        foreach (var slice in _taskbarShade) slice.Place(0, 0, 1, 1, 0);
    }

    // The taskbar without the parts under full-screen windows, which lie over it as on the
    // screen: `covers` are the spans those windows take now (along x when `horizontal`, else y).
    void PlaceTaskbar(bool horizontal, IEnumerable<(float From, float To)> covers)
    {
        if (_taskbar is not { } t) return;
        var f = Frame(t.Window);
        float x = f.Left - _overlay.Area.Left, y = f.Top - _overlay.Area.Top;
        float a0 = horizontal ? x : y, a1 = a0 + (horizontal ? f.Width : f.Height);
        bool shadeStart = false, shadeEnd = false;
        foreach (var (c0, c1) in covers)
        {
            if (c1 <= a0 || c0 >= a1) continue;
            if (c0 <= a0) { a0 = Math.Max(a0, c1); shadeStart = true; }
            else if (c1 >= a1) { a1 = Math.Min(a1, c0); shadeEnd = true; }
            else if (c0 - a0 >= a1 - c1) { a1 = c0; shadeEnd = true; }
            else { a0 = c1; shadeStart = true; }
        }
        float step = 2.5f * _overlay.Scale, width = Shade.Length * step;
        float m0 = shadeStart ? a0 + width : a0, m1 = shadeEnd && !shadeStart ? a1 - width : a1;
        t.PlaceCut(x, y, f.Width, f.Height, m0, Math.Max(m0, m1), horizontal);
        for (int i = 0; i < _taskbarShade.Count; i++)
        {
            if (!(shadeStart || shadeEnd)) { _taskbarShade[i].Place(0, 0, 1, 1, 0); continue; }
            float s0 = shadeStart ? a0 + i * step : a1 - (i + 1) * step, s1 = s0 + step;
            s0 = Math.Clamp(s0, a0, a1); s1 = Math.Clamp(s1, a0, a1);
            if (s1 - s0 >= 0.5f)
            {
                if (horizontal) _gfx.Rect(s0, y, s1 - s0, f.Height, 0xFF000000);
                else _gfx.Rect(x, s0, f.Width, s1 - s0, 0xFF000000);
            }
            _taskbarShade[i].PlaceCut(x, y, f.Width, f.Height, s0, s1, horizontal, Shade[i]);
        }
    }

    // The top of the screen below the taskbar, in the overlay's own coordinates.
    float WorkTop => _overlay.Work.Top - _overlay.Area.Top;

    // The overlay's part of the monitor, in the pixels of a picture of the whole monitor.
    RECT Inner => new(_overlay.Area.Left - _overlay.Monitor.Left, _overlay.Area.Top - _overlay.Monitor.Top,
        _overlay.Area.Right - _overlay.Monitor.Left, _overlay.Area.Bottom - _overlay.Monitor.Top);

    ID2D1Bitmap1? Picture(Guid desktop)
    {
        if (_bitmaps.TryGetValue(desktop, out var b)) return b;
        if (_snapshots.TryGetValue(desktop, out var p) && p.Width == _overlay.Monitor.Width && p.Height == _overlay.Monitor.Height)
            return _bitmaps[desktop] = _gfx.Bitmap(p, Inner);
        return Wallpaper(desktop)?.Sharp;
    }

    ID2D1Bitmap1? TilePicture(Guid desktop, float w, float h)
    {
        if (_tilePictures.TryGetValue(desktop, out var t) && Math.Abs(t.PixelSize.Width - (int)w) <= 1) return t;
        t?.Dispose();
        var full = Picture(desktop);
        if (full == null) return null;
        return _tilePictures[desktop] = _gfx.Scaled(full, (int)w, (int)h, 8 * _overlay.Scale);
    }

    (ID2D1Bitmap1 Sharp, ID2D1Bitmap1 Blurred)? Wallpaper(Guid desktop)
    {
        if (_wallpapers.TryGetValue(desktop, out var w)) return w;
        (Pixels Sharp, Pixels Blurred, string? Path) px;
        bool ready;
        lock (_wallpaperPixels) ready = _wallpaperPixels.TryGetValue(desktop, out px);
        var m = _overlay.Monitor;
        if (!ready || px.Sharp.Width != m.Width || px.Sharp.Height != m.Height) px = MakeWallpaper(desktop, m);
        var inner = Inner;
        double k = (double)px.Blurred.Width / px.Sharp.Width;
        var small = new RECT((int)(inner.Left * k), (int)(inner.Top * k), (int)Math.Ceiling(inner.Right * k), (int)Math.Ceiling(inner.Bottom * k));
        return _wallpapers[desktop] = (_gfx.Bitmap(px.Sharp, inner), _gfx.Bitmap(px.Blurred, small));
    }

    // The wallpaper on the whole monitor, sharp and blurred.
    (Pixels Sharp, Pixels Blurred, string? Path) MakeWallpaper(Guid desktop, RECT monitor)
    {
        string? path = Capture.WallpaperPath(desktop);
        using var full = Capture.Wallpaper(path, monitor.Width, monitor.Height);
        var px = (Capture.FromBitmap(full), Capture.Blurred(full), path);
        lock (_wallpaperPixels) _wallpaperPixels[desktop] = px;
        return px;
    }

    // Pictures of the desktops Lanes has none of (it just started), made from their windows (see
    // Capture.Composed), so the first swipe to one shows it as it is rather than its bare
    // wallpaper. A real picture replaces each once you are there.
    void ComposeMissing()
    {
        if (!Ready) return;
        var monitor = _overlay.Monitor;
        var jobs = Desktops.All().Where(d => d != _current && !_snapshots.ContainsKey(d))
            .Select(d => (Desktop: d, Windows: AppWindows.OnScreen(monitor, _overlay.Handle, d))).ToList();
        if (jobs.Count == 0) return;
        Task.Run(() =>
        {
            long t0 = Environment.TickCount64;
            foreach (var (d, windows) in jobs)
                try
                {
                    var picture = Capture.Composed(WallpaperPixels(d, monitor), windows, monitor);
                    var front = windows.Count > 0 ? windows[^1] : null;
                    bool full = front != null && front.Frame.Left <= monitor.Left && front.Frame.Top <= monitor.Top
                        && front.Frame.Right >= monitor.Right && front.Frame.Bottom >= monitor.Bottom;
                    Post(() =>
                    {
                        if (_snapshots.ContainsKey(d)) { picture.Dispose(); return; }
                        _snapshots[d] = picture;
                        if (_bitmaps.Remove(d, out var bmp)) bmp.Dispose();
                        if (_tilePictures.Remove(d, out var tile)) tile.Dispose();
                        _fullScreen[d] = full;
                        if (front != null) _appOf.TryAdd(d, front.App);
                    });
                }
                catch (Exception e) { Log.Write($"picture of desktop {Desktops.IndexOf(d) + 1}: {e.Message}"); }
            Log.Write($"pictures of {jobs.Count} desktops made from their windows in {Environment.TickCount64 - t0} ms");
        });
    }

    // Makes the wallpapers ahead of time, and again for a desktop whose picture file changed
    // (you picked another, or Windows gave a new desktop its own a moment after it was made).
    void PrepareWallpapers()
    {
        if (!Ready) return;
        var all = Desktops.All();
        var monitor = _overlay.Monitor;
        Task.Run(() =>
        {
            foreach (var d in all)
                try
                {
                    (Pixels Sharp, Pixels Blurred, string? Path) had;
                    bool have;
                    lock (_wallpaperPixels) have = _wallpaperPixels.TryGetValue(d, out had);
                    if (have && had.Path == Capture.WallpaperPath(d)) continue;
                    MakeWallpaper(d, monitor);
                    if (have) Post(() => WallpaperChanged(d));
                }
                catch (Exception e) { Log.Write($"wallpaper: {e.Message}"); }
        });
    }

    void WallpaperChanged(Guid desktop)
    {
        Log.Write($"wallpaper of desktop {Desktops.IndexOf(desktop) + 1} changed");
        if (_wallpapers.Remove(desktop, out var w)) { w.Sharp.Dispose(); w.Blurred.Dispose(); }
        if (_deskPixels.Remove(desktop, out var desk)) desk.Pixels.Dispose();
        if (_deskBitmaps.Remove(desktop, out var db)) db.Dispose();
        if (!_snapshots.ContainsKey(desktop) && _tilePictures.Remove(desktop, out var t)) t.Dispose();
        _redraw = true;
    }

    // ---------- touchpad ----------

    void OnFrame(IntPtr device, TouchFrame frame)
    {
        if (!Ready || !_settings.Gestures) return;
        int fingers = frame.Fingers.Length;
        _lastReport[device] = Environment.TickCount64;
        _fingersOn.TryGetValue(device, out int before);
        _fingersOn[device] = fingers;
        // Three or more fingers just landed: take a picture of the screen now, while they
        // travel far enough for the swipe to be recognised.
        if (fingers >= 3 && before < 3 && !_overlay.Shown && _switch == null)
        {
            DiscardTouchCapture();
            var monitor = PointerMonitor().Monitor;
            var own = _overlay.Handle;
            _touchCaptureOf = _current;
            _touchCapture = Task.Run(() =>
            {
                long t0 = Environment.TickCount64;
                var p = Capture.ScreenWithout(monitor, own, out var keep);
                _captureMs = Environment.TickCount64 - t0;
                return (p, keep);
            });
        }
        if (!_recognizers.TryGetValue(device, out var r)) _recognizers[device] = r = new GestureRecognizer(this);
        if (_activeDevice != IntPtr.Zero && _activeDevice != device && !r.Tracking) return;
        _activeDevice = device;
        // In the open overview two fingers slide another lane's row; elsewhere they scroll.
        r.TwoFingers = _overview is { Open: true };
        r.OnFrame(frame);
        if (!r.Tracking && fingers == 0) _activeDevice = IntPtr.Zero;
    }

    // A synthetic report, for the demo run on machines without a touchpad.
    public void Feed(TouchFrame frame) => Post(() => OnFrame((IntPtr)1, frame with { Time = Environment.TickCount64 }));

    public void Begin(GestureKind kind, Axis axis)
    {
        _ignoring = false;
        if (kind == GestureKind.Scroll)
        {
            if (!BeginRowScroll(axis)) _ignoring = true;
            return;
        }
        if (kind == GestureKind.Switch)
        {
            // Four fingers in the open overview move between desktops without leaving it.
            if (_overview is { Open: true, Drag: null, Going: null }) { BeginInnerSwipe(axis); return; }
            if (_overview != null || _switch is { Spring: null }) { _ignoring = true; return; }
            BeginSwitch(axis);
        }
        else BeginOverview();
    }

    public void Move(double dx, double dy)
    {
        if (_ignoring) return;
        if (_switch != null) MoveSwitch(dx, dy);
        else if (_overview?.Swipe != null) MoveInnerSwipe(dx, dy);
        else if (_overview?.Scroll != null) MoveRowScroll(dx);
        else if (_overview != null) MoveOverview(dy);
    }

    // How the swipe that just ended was reported, for the log.
    string Reported()
    {
        var r = _recognizers.Values.FirstOrDefault(x => x.Reports > 0);
        if (r == null) return "";
        return $" ({r.Reports} reports in {r.TrackedMs} ms, {(r.EndedBySilence ? "reports stopped" : "lift seen")}, {_touchpad.Dropped} empty reports so far)";
    }

    public void End(double vx, double vy)
    {
        if (_ignoring) { _ignoring = false; return; }
        if (_switch != null) EndSwitch(vx, vy);
        else if (_overview?.Swipe is { Spring: null }) EndInnerSwipe(vx, vy);
        else if (_overview?.Scroll != null) EndRowScroll(vx);
        else if (_overview != null) EndOverview(vy);
    }

    // A picture taken at touch time that no swipe used: free it once it is done.
    void DiscardTouchCapture()
    {
        var t = _touchCapture;
        _touchCapture = null;
        t?.ContinueWith(x => { if (x.Status == TaskStatus.RanToCompletion) x.Result.Pixels.Dispose(); });
    }

    long _captureMs;
    // How long the last swipe's first frame waited for the picture of the screen, for the log.
    public string CaptureNote = "";

    // The picture of the screen for a gesture that starts now; windows that asked to stay live
    // over it will be kept above the overlay.
    Pixels TakeTouchCapture()
    {
        long t0 = Environment.TickCount64;
        Pixels? snap = null;
        IntPtr keep = IntPtr.Zero;
        try
        {
            if (_touchCapture != null && _touchCaptureOf == _current && _touchCapture.Wait(150))
            {
                (snap, keep) = _touchCapture.Result;
                _touchCapture = null;
            }
        }
        catch { }
        DiscardTouchCapture();
        bool fresh = snap == null;
        snap ??= Capture.ScreenWithout(PointerMonitor().Monitor, _overlay.Handle, out keep);
        _overlay.Under = keep;
        CaptureNote = $"screen picture {(fresh ? "taken late" : $"taken in {_captureMs} ms")}, first frame waited {Environment.TickCount64 - t0} ms";
        return snap;
    }

    public void OpenOverviewFromMenu() => Post(OpenOverview);

    void OpenOverview()
    {
        if (!Ready || _overview != null || _switch != null) return;
        _touchCaptureOf = _current;
        BeginOverview();
        _overview!.Tracking = false;
        SettleOverview(1, 0);
    }

    // The overview key: opens it, or closes it when it is open.
    void ToggleOverview()
    {
        Log.Write("overview asked for by another program");
        if (_overview != null) CloseOverview();
        else OpenOverview();
    }

    // Takes a screenshot of the whole screen, overlay included (demo runs only).
    public void Screenshot(string path) => Post(() =>
    {
        Render();
        DwmFlush();
        DwmFlush();
        var mi = PrimaryMonitor();
        using var p = Capture.Screen(mi.Monitor);
        using var bmp = new System.Drawing.Bitmap(p.Width, p.Height, p.Width * 4, System.Drawing.Imaging.PixelFormat.Format32bppRgb, p.Data);
        bmp.Save(path, System.Drawing.Imaging.ImageFormat.Png);
        Log.Write($"screenshot {path}");
    });

    public void Snapshot() => Post(() => { _lastSnapshot = 0; _current = Desktops.Current; Idle(); });

    // Runs on the engine thread and hands back the result.
    public Task<T> Ask<T>(Func<T> f)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        Post(() => { try { tcs.SetResult(f()); } catch (Exception e) { tcs.SetException(e); } });
        return tcs.Task;
    }

    // A new desktop at the end of a lane; returns the lanes as they are now.
    public LaneMap AddDesktop(int lane)
    {
        var made = Desktops.Create();
        if (made != Guid.Empty) { _lanes.Add(made, lane); _lanes.Made.Add(made); }
        SyncOrder();
        _lanes.Reconcile(Desktops.All(), Desktops.Current);
        Settings.SaveLanes(_lanes);
        return LaneMap.Load(_lanes.Save());
    }

    // For the demo run's log; call on the engine thread (through Ask).
    public int LaneOf(Guid desktop) => _lanes.Locate(desktop)?.Lane ?? -1;
    public string LanesText() => string.Join(" | ", _lanes.Lanes.Select(l => l.Desktops.Count)) + " desktops";

    sealed class MessageWindow : NativeWindow
    {
        readonly Engine _e;

        public MessageWindow(Engine e)
        {
            _e = e;
            CreateHandle(new CreateParams { Parent = new IntPtr(-3) });
        }

        protected override void WndProc(ref Message m)
        {
            switch (m.Msg)
            {
                case WM_INPUT:
                    try { _e._touchpad.OnInput(m.LParam); }
                    catch (Exception ex) { Log.Write($"input: {ex.Message}"); }
                    break;
                case WM_COMMAND_QUEUED:
                    while (_e._commands.TryDequeue(out var a))
                        try { a(); }
                        catch (Exception ex) { Log.Write($"command: {ex}"); }
                    break;
                case WM_DESKTOP_CHANGED:
                    _e.OnDesktopChanged();
                    break;
            }
            base.WndProc(ref m);
        }
    }
}
