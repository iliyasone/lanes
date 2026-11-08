using System.Collections.Concurrent;
using System.Diagnostics;
using Lanes.Core;
using Vortice.Direct2D1;
using static Lanes.Native;

namespace Lanes;

// Everything that runs while you swipe lives on one thread: the touchpad reports arrive here,
// the gesture is recognised here, and each frame is drawn here right after the freshest report.
sealed partial class Engine : IGestureSink
{
    const int WM_COMMAND_QUEUED = WM_USER + 1, WM_DESKTOP_CHANGED = WM_USER + 2;
    const float GapDip = 24;

    readonly Settings _settings;
    readonly Tuning _tuning = new();
    readonly bool _demo;
    readonly ConcurrentQueue<Action> _commands = new();
    readonly Stopwatch _clock = Stopwatch.StartNew();
    Thread? _thread;
    MessageWindow _msg = null!;
    Overlay _overlay = null!;
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
    readonly Dictionary<Guid, (Pixels Sharp, Pixels Blurred)> _wallpaperPixels = new();
    readonly Dictionary<Guid, (ID2D1Bitmap1 Sharp, ID2D1Bitmap1 Blurred)> _wallpapers = new();
    Task<Pixels>? _touchCapture;
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
            _overlay.PickArea();
            _gfx = new Gfx();
            _gfx.Attach(_overlay.Handle, _overlay.Area.Width, _overlay.Area.Height);
            _overlay.MouseMove += OnMouseMove;
            _overlay.LeftDown += OnLeftDown;
            _overlay.LeftUp += OnLeftUp;
            _overlay.MiddleUp += OnMiddleUp;
            _touchpad = new Touchpad(OnFrame);
            _touchpad.Register(_msg.Handle);
            if (Desktops.Load())
            {
                Desktops.Pin(_overlay.Handle);
                Desktops.Listen(_msg.Handle, WM_DESKTOP_CHANGED);
                SetUpLanes();
                Ready = true;
            }
            else Status?.Invoke("This Windows version is not supported");
            PrepareWallpapers();
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
                }
                else
                {
                    MessageLoop.Wait(200);
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
        _overlay.Hide();
    }

    bool Animating => _switch != null || _overview is { } o && (o.Tracking || o.Spring != null || o.Zoom != null || o.Swipe != null || o.Moving) || _redraw;
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
        _current = Desktops.Current;
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
        if (!Ready || _overlay.Shown || _switch != null || _overview != null) return;
        long now = Environment.TickCount64;
        if (now - _lastSnapshot < 5000 || Touching) return;
        _lastSnapshot = now;
        Remember(_current, Capture.Screen(_overlay.PickArea()));
        Warm();
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
        if (_snapshots.TryGetValue(desktop, out var old) && old != pixels) old.Dispose();
        _snapshots[desktop] = pixels;
        if (_bitmaps.Remove(desktop, out var bmp)) bmp.Dispose();
        if (_tilePictures.Remove(desktop, out var tile)) tile.Dispose();
        var fg = GetForegroundWindow();
        if (fg != IntPtr.Zero && Desktops.OnCurrent(fg)) _appOf[desktop] = AppWindows.AppName(fg);
    }

    ID2D1Bitmap1? Picture(Guid desktop)
    {
        if (_bitmaps.TryGetValue(desktop, out var b)) return b;
        if (_snapshots.TryGetValue(desktop, out var p) && p.Width == _gfx.Width && p.Height == _gfx.Height)
            return _bitmaps[desktop] = _gfx.Bitmap(p);
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
        (Pixels Sharp, Pixels Blurred) px;
        bool ready;
        lock (_wallpaperPixels) ready = _wallpaperPixels.TryGetValue(desktop, out px);
        if (!ready) px = MakeWallpaper(desktop);
        return _wallpapers[desktop] = (_gfx.Bitmap(px.Sharp), _gfx.Bitmap(px.Blurred));
    }

    (Pixels Sharp, Pixels Blurred) MakeWallpaper(Guid desktop)
    {
        var area = _overlay.Area;
        var mi = MonitorOf(MonitorFromPoint(new POINT { X = area.Left + 1, Y = area.Top + 1 }, MONITOR_DEFAULTTONEAREST));
        using var full = Capture.Wallpaper(desktop, mi.Monitor.Width, mi.Monitor.Height);
        using var work = full.Clone(new System.Drawing.Rectangle(area.Left - mi.Monitor.Left, area.Top - mi.Monitor.Top, area.Width, area.Height), System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
        var px = (Capture.FromBitmap(work), Capture.Blurred(work));
        lock (_wallpaperPixels) _wallpaperPixels[desktop] = px;
        return px;
    }

    void PrepareWallpapers()
    {
        if (!Ready) return;
        var all = Desktops.All();
        Task.Run(() =>
        {
            foreach (var d in all)
                try
                {
                    bool have;
                    lock (_wallpaperPixels) have = _wallpaperPixels.ContainsKey(d);
                    if (!have) MakeWallpaper(d);
                }
                catch (Exception e) { Log.Write($"wallpaper: {e.Message}"); }
        });
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
            var area = _overlay.PickArea();
            _touchCaptureOf = _current;
            _touchCapture = Task.Run(() =>
            {
                long t0 = Environment.TickCount64;
                var p = Capture.Screen(area);
                _captureMs = Environment.TickCount64 - t0;
                return p;
            });
        }
        if (!_recognizers.TryGetValue(device, out var r)) _recognizers[device] = r = new GestureRecognizer(this);
        if (_activeDevice != IntPtr.Zero && _activeDevice != device && !r.Tracking) return;
        _activeDevice = device;
        r.OnFrame(frame);
        if (!r.Tracking && fingers == 0) _activeDevice = IntPtr.Zero;
    }

    // A synthetic report, for the demo run on machines without a touchpad.
    public void Feed(TouchFrame frame) => Post(() => OnFrame((IntPtr)1, frame with { Time = Environment.TickCount64 }));

    public void Begin(GestureKind kind, Axis axis)
    {
        _ignoring = false;
        if (kind == GestureKind.Switch)
        {
            // Four fingers in the open overview move between desktops without leaving it.
            if (_overview is { Open: true, Drag: null }) { BeginInnerSwipe(axis); return; }
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
        else if (_overview != null) EndOverview(vy);
    }

    // A picture taken at touch time that no swipe used: free it once it is done.
    void DiscardTouchCapture()
    {
        var t = _touchCapture;
        _touchCapture = null;
        t?.ContinueWith(x => { if (x.Status == TaskStatus.RanToCompletion) x.Result.Dispose(); });
    }

    long _captureMs;
    // How long the last swipe's first frame waited for the picture of the screen, for the log.
    public string CaptureNote = "";

    Pixels TakeTouchCapture()
    {
        long t0 = Environment.TickCount64;
        Pixels? snap = null;
        try
        {
            if (_touchCapture != null && _touchCaptureOf == _current && _touchCapture.Wait(150))
            {
                snap = _touchCapture.Result;
                _touchCapture = null;
            }
        }
        catch { }
        DiscardTouchCapture();
        bool fresh = snap == null;
        snap ??= Capture.Screen(_overlay.PickArea());
        CaptureNote = $"screen picture {(fresh ? "taken late" : $"taken in {_captureMs} ms")}, first frame waited {Environment.TickCount64 - t0} ms";
        return snap;
    }

    public void OpenOverviewFromMenu() => Post(() =>
    {
        if (!Ready || _overview != null || _switch != null) return;
        _overlay.PickArea();
        _touchCaptureOf = _current;
        BeginOverview();
        _overview!.Tracking = false;
        SettleOverview(1, 0);
    });

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
