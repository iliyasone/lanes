using System.Diagnostics;
using System.Runtime.InteropServices;
using Lanes.Core;
using static Lanes.Native;

namespace Lanes;

// Shadows under the app windows on the desktop, as on a Mac and as the overview draws them
// (ShadowLook), so opening the overview changes nothing about them; Windows' own are thin.
// Each window gets four see-through windows of Lanes' own round it, which clicks pass through:
// above it, below it and at its sides (under the window itself nothing is needed). They are kept
// just under it in Windows' order of windows and move with it. Windows tells Lanes the moment a
// window moves, on a thread that does nothing else, so the shadow keeps up as closely as Windows
// lets it: while a window is dragged it can be a frame behind.
// Only the windows on the desktop you are on have shadows; a window gets its shadow again when
// its desktop comes up. Maximized and full-screen windows have none (it would be off the
// screen), nor windows kept on top of the others (their shadow would have to be on top too).
sealed class DesktopShadows : IDisposable
{
    sealed class Shade
    {
        public IntPtr Window;
        public readonly IntPtr[] Pieces = new IntPtr[4];
        public readonly bool[] Empty = new bool[4];
        public PixelRect Frame;
        public double Scale;
        public bool Drawn, Shown;
        // A window that has just opened or come back from the taskbar: its shadow comes in a
        // moment after it, as Windows' animation of it ends (when it began; 0 once it is in).
        public double FadeFrom;
        public float Alpha = 1;
    }

    readonly Dictionary<IntPtr, Shade> _shades = new();
    // Windows just shown or brought back from the taskbar, and when.
    readonly Dictionary<IntPtr, double> _arriving = new();
    readonly Stopwatch _clock = Stopwatch.StartNew();
    readonly Thread _thread;
    readonly uint _pid = (uint)Environment.ProcessId;
    readonly List<IntPtr> _hooks = new();
    WinEventProc? _proc;
    IntPtr _instance;
    uint _threadId;
    double _dueAt;
    long _lastError;

    double Now => _clock.Elapsed.TotalSeconds;

    public DesktopShadows()
    {
        _thread = new Thread(Run) { IsBackground = true, Name = "window shadows", Priority = ThreadPriority.Highest };
        _thread.Start();
    }

    public void Dispose()
    {
        if (_threadId != 0) PostThreadMessage(_threadId, 0x0012 /* WM_QUIT */, IntPtr.Zero, IntPtr.Zero);
        _thread.Join(1000);
    }

    // One look per screen scale, shared with the pictures Lanes makes of other desktops.
    static readonly Dictionary<double, ShadowLook> Looks = new();

    public static ShadowLook Look(double scale)
    {
        lock (Looks)
        {
            if (!Looks.TryGetValue(scale, out var look)) Looks[scale] = look = new ShadowLook(scale);
            return look;
        }
    }

    // ---------- the thread ----------

    const uint EVENT_SYSTEM_FOREGROUND = 0x0003, EVENT_SYSTEM_MINIMIZESTART = 0x0016, EVENT_SYSTEM_MINIMIZEEND = 0x0017,
        EVENT_OBJECT_DESTROY = 0x8001, EVENT_OBJECT_SHOW = 0x8002, EVENT_OBJECT_HIDE = 0x8003, EVENT_OBJECT_REORDER = 0x8004,
        EVENT_OBJECT_LOCATIONCHANGE = 0x800B;
    // Foreground; move or resize ended; minimize start and end; destroy, show, hide, reorder;
    // location change; cloaked and uncloaked.
    static readonly (uint Min, uint Max)[] Events = { (0x0003, 0x0003), (0x000B, 0x000B), (0x0016, 0x0017), (0x8001, 0x8004), (0x800B, 0x800B), (0x8017, 0x8018) };

    void Run()
    {
        _threadId = GetCurrentThreadId();
        MessageLoop.PeekMessage(out _, IntPtr.Zero, 0, 0, 0);
        try
        {
            RegisterPieceClass();
            _proc = OnEvent;
            foreach (var (min, max) in Events)
                _hooks.Add(SetWinEventHook(min, max, IntPtr.Zero, _proc, 0, 0, 0 /* WINEVENT_OUTOFCONTEXT */ | 2 /* WINEVENT_SKIPOWNPROCESS */));
            // Windows calls OnEvent from inside GetMessage, at once; a timer wakes the loop for
            // the look round all windows (every half second, sooner after events) and for fades.
            while (true)
            {
                try
                {
                    double now = Now;
                    if (now >= _dueAt)
                    {
                        _dueAt = now + 0.5;
                        Reconcile();
                    }
                    Arm(StepFades(now) ? 0.015 : _dueAt - Now);
                }
                catch (Exception e) { Failed(e); }
                if (MessageLoop.GetMessage(out var msg, IntPtr.Zero, 0, 0) <= 0) break;
                if (msg.Message != 0x0113 /* WM_TIMER */)
                {
                    MessageLoop.TranslateMessage(ref msg);
                    MessageLoop.DispatchMessage(ref msg);
                }
            }
        }
        catch (Exception e) { Log.Write($"window shadows stopped: {e}"); }
        foreach (var h in _hooks) UnhookWinEvent(h);
        foreach (var sh in _shades.Values) Destroy(sh);
        _shades.Clear();
    }

    void Failed(Exception e)
    {
        // Into the log, but not on every event of a storm.
        if (Environment.TickCount64 - _lastError < 10000) return;
        _lastError = Environment.TickCount64;
        Log.Write($"window shadows: {e}");
    }

    UIntPtr _timer;

    void Arm(double seconds) => _timer = SetTimer(IntPtr.Zero, _timer, (uint)Math.Clamp(seconds * 1000, 1, 5000), IntPtr.Zero);

    void Soon(double seconds)
    {
        if (Now + seconds >= _dueAt) return;
        _dueAt = Now + seconds;
        Arm(seconds);
    }

    void OnEvent(IntPtr hook, uint ev, IntPtr hwnd, int obj, int child, uint thread, uint time)
    {
        try
        {
            if (ev == EVENT_OBJECT_REORDER) { Soon(0.03); return; }
            if (hwnd == IntPtr.Zero || obj != 0 /* OBJID_WINDOW */ || child != 0) return;
            bool known = _shades.TryGetValue(hwnd, out var sh);
            switch (ev)
            {
                case EVENT_OBJECT_LOCATIONCHANGE:
                    // The one that comes many times a second while a window is dragged: answered at once.
                    if (known) Follow(sh!);
                    else if (TopLevel(hwnd)) Soon(0.05);
                    break;
                case EVENT_SYSTEM_MINIMIZESTART:
                    if (known) Hide(sh!);
                    break;
                case EVENT_OBJECT_SHOW:
                case EVENT_SYSTEM_MINIMIZEEND:
                    if (TopLevel(hwnd))
                    {
                        _arriving[hwnd] = Now;
                        Soon(0.01);
                    }
                    break;
                case EVENT_OBJECT_HIDE:
                case EVENT_OBJECT_DESTROY:
                    if (known) Drop(hwnd);
                    break;
                case EVENT_SYSTEM_FOREGROUND:
                    // Brought to the front: its shadow goes with it now, the rest is looked at soon.
                    if (known && sh!.Shown) Stack(sh, show: false);
                    Soon(0.03);
                    break;
                default:
                    if (known || TopLevel(hwnd)) Soon(0.01);
                    break;
            }
        }
        catch (Exception e) { Failed(e); }
    }

    static bool TopLevel(IntPtr hwnd) => GetAncestor(hwnd, 1 /* GA_PARENT */) == GetDesktopWindow();

    // ---------- which windows, and keeping up with them ----------

    void Reconcile()
    {
        var want = new List<IntPtr>(); // front to back
        EnumWindows((h, _) =>
        {
            if (Wanted(h)) want.Add(h);
            return true;
        }, IntPtr.Zero);
        var wanted = want.ToHashSet();
        foreach (var h in _shades.Keys.Where(h => !wanted.Contains(h)).ToList()) Drop(h);
        double now = Now;
        foreach (var h in want)
        {
            _shades.TryGetValue(h, out var sh);
            // Its pieces stayed on another desktop: the window came here from there.
            if (sh is { Shown: true } && CloakedByShell(sh.Pieces[0]))
            {
                Drop(h);
                sh = null;
            }
            if (sh == null)
            {
                if (Make(h) is not { } made) continue;
                _shades[h] = sh = made;
                if (_arriving.TryGetValue(h, out var at) && now - at < 1) { sh.FadeFrom = now; sh.Alpha = 0; }
                // Windows may still have been switching desktops as they were made: look again.
                Soon(0.15);
            }
            Follow(sh);
        }
        foreach (var h in _arriving.Where(a => now - a.Value > 1).Select(a => a.Key).ToList()) _arriving.Remove(h);
        foreach (var h in want)
            if (_shades.TryGetValue(h, out var sh) && sh.Shown && !Stacked(sh)) Stack(sh, show: false);
    }

    bool Wanted(IntPtr h)
    {
        if (!IsWindowVisible(h) || IsIconic(h) || IsZoomed(h) || Cloaked(h)) return false;
        if ((GetWindowLongPtr(h, GWL_EXSTYLE).ToInt64() & WS_EX_TOPMOST) != 0) return false;
        GetWindowThreadProcessId(h, out uint pid);
        if (pid == _pid) return false;
        return AppWindows.IsApp(h, out var f, out _) && !FillsMonitor(h, f);
    }

    // Whether a window of another desktop would have a shadow there (for Lanes' picture of it).
    public static bool Casts(IntPtr h, RECT frame) =>
        !IsZoomed(h) && (GetWindowLongPtr(h, GWL_EXSTYLE).ToInt64() & WS_EX_TOPMOST) == 0 && !FillsMonitor(h, frame);

    static bool FillsMonitor(IntPtr h, RECT f)
    {
        var m = MonitorOf(MonitorFromWindow(h, MONITOR_DEFAULTTONEAREST)).Monitor;
        return f.Left <= m.Left && f.Top <= m.Top && f.Right >= m.Right && f.Bottom >= m.Bottom;
    }

    // Puts the shadow where its window is now: moves the pieces, or draws them again when the
    // window's size (or its screen's scale) changed.
    void Follow(Shade sh)
    {
        var h = sh.Window;
        var r = Frame(h);
        if (!IsWindowVisible(h) || IsIconic(h) || IsZoomed(h) || FillsMonitor(h, r))
        {
            Hide(sh);
            Soon(0.01);
            return;
        }
        var frame = new PixelRect(r.Left, r.Top, r.Width, r.Height);
        double scale = Math.Max(96, GetDpiForWindow(h)) / 96.0;
        var look = Look(scale);
        var pieces = look.Pieces(frame);
        if (!sh.Drawn || frame.W != sh.Frame.W || frame.H != sh.Frame.H || scale != sh.Scale)
        {
            for (int i = 0; i < 4; i++) Paint(sh, i, pieces[i], frame, look);
            sh.Drawn = true;
        }
        else if (frame.X != sh.Frame.X || frame.Y != sh.Frame.Y)
        {
            var dwp = BeginDeferWindowPos(4);
            for (int i = 0; i < 4 && dwp != IntPtr.Zero; i++)
                if (!sh.Empty[i]) dwp = DeferWindowPos(dwp, sh.Pieces[i], IntPtr.Zero, pieces[i].X, pieces[i].Y, 0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
            if (dwp != IntPtr.Zero) EndDeferWindowPos(dwp);
        }
        sh.Frame = frame;
        sh.Scale = scale;
        if (!sh.Shown)
        {
            Stack(sh, show: true);
            sh.Shown = true;
        }
    }

    // The pieces go just under the window, in one step (and come into sight with `show`).
    void Stack(Shade sh, bool show)
    {
        IntPtr after = sh.Window;
        var dwp = BeginDeferWindowPos(4);
        for (int i = 0; i < 4 && dwp != IntPtr.Zero; i++)
        {
            uint flags = SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | (show && !sh.Empty[i] ? SWP_SHOWWINDOW : 0);
            dwp = DeferWindowPos(dwp, sh.Pieces[i], after, 0, 0, 0, 0, flags);
            after = sh.Pieces[i];
        }
        if (dwp != IntPtr.Zero) EndDeferWindowPos(dwp);
    }

    bool Stacked(Shade sh)
    {
        IntPtr prev = sh.Window;
        foreach (var p in sh.Pieces)
        {
            if (GetWindow(prev, 2 /* GW_HWNDNEXT */) != p) return false;
            prev = p;
        }
        return true;
    }

    void Hide(Shade sh)
    {
        if (!sh.Shown) return;
        foreach (var p in sh.Pieces) ShowWindow(p, SW_HIDE);
        sh.Shown = false;
    }

    void Drop(IntPtr h)
    {
        if (_shades.Remove(h, out var sh)) Destroy(sh);
    }

    static void Destroy(Shade sh)
    {
        foreach (var p in sh.Pieces)
            if (p != IntPtr.Zero) DestroyWindow(p);
    }

    bool StepFades(double now)
    {
        bool any = false;
        foreach (var sh in _shades.Values)
        {
            if (sh.FadeFrom <= 0) continue;
            float a = (float)Math.Clamp((now - sh.FadeFrom - FadeDelay) / FadeTime, 0, 1);
            if (a >= 1) sh.FadeFrom = 0; else any = true;
            if (Math.Abs(a - sh.Alpha) < 0.001f || !sh.Drawn) continue;
            sh.Alpha = a;
            var blend = new BLENDFUNCTION { SourceConstantAlpha = (byte)(a * 255), AlphaFormat = 1 /* AC_SRC_ALPHA */ };
            for (int i = 0; i < 4; i++)
                if (!sh.Empty[i]) UpdateLayeredWindow(sh.Pieces[i], IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 0, ref blend, 2 /* ULW_ALPHA */);
        }
        return any;
    }

    const double FadeDelay = 0.1, FadeTime = 0.2;

    // ---------- the pieces ----------

    const string PieceClass = "Lanes shadow";

    void RegisterPieceClass()
    {
        _instance = GetModuleHandle(null);
        var wc = new WNDCLASSEX
        {
            Size = Marshal.SizeOf<WNDCLASSEX>(),
            WndProc = GetProcAddress(GetModuleHandle("user32.dll"), "DefWindowProcW"),
            Instance = _instance,
            ClassName = PieceClass,
        };
        RegisterClassEx(ref wc);
    }

    Shade? Make(IntPtr h)
    {
        var sh = new Shade { Window = h };
        for (int i = 0; i < 4; i++)
        {
            // Never active, see-through where nothing is drawn, clicks pass through.
            sh.Pieces[i] = CreateWindowEx((int)(WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE), PieceClass, "",
                unchecked((int)WS_POPUP), 0, 0, 1, 1, IntPtr.Zero, IntPtr.Zero, _instance, IntPtr.Zero);
            if (sh.Pieces[i] == IntPtr.Zero)
            {
                Destroy(sh);
                return null;
            }
        }
        return sh;
    }

    // Draws one piece of the shadow of a window filling `frame` and puts it at `at`.
    void Paint(Shade sh, int i, PixelRect at, PixelRect frame, ShadowLook look)
    {
        var hwnd = sh.Pieces[i];
        bool empty = at.W <= 0 || at.H <= 0;
        if (empty != sh.Empty[i])
        {
            sh.Empty[i] = empty;
            if (sh.Shown) ShowWindow(hwnd, empty ? SW_HIDE : SW_SHOWNOACTIVATE);
        }
        if (empty) return;
        IntPtr screen = GetDC(IntPtr.Zero), mem = CreateCompatibleDC(screen);
        var bi = new BITMAPINFOHEADER { Size = Marshal.SizeOf<BITMAPINFOHEADER>(), Width = at.W, Height = -at.H, Planes = 1, BitCount = 32 };
        IntPtr dib = CreateDIBSection(mem, ref bi, 0, out IntPtr bits, IntPtr.Zero, 0);
        if (dib != IntPtr.Zero)
        {
            unsafe { look.Draw(new Span<uint>((void*)bits, at.W * at.H), at.W, at, frame); }
            IntPtr old = SelectObject(mem, dib);
            var pos = new POINT { X = at.X, Y = at.Y };
            var size = new SIZE { Cx = at.W, Cy = at.H };
            var from = new POINT();
            var blend = new BLENDFUNCTION { SourceConstantAlpha = (byte)(sh.Alpha * 255), AlphaFormat = 1 /* AC_SRC_ALPHA */ };
            UpdateLayeredWindow(hwnd, screen, ref pos, ref size, mem, ref from, 0, ref blend, 2 /* ULW_ALPHA */);
            SelectObject(mem, old);
            DeleteObject(dib);
        }
        DeleteDC(mem);
        ReleaseDC(IntPtr.Zero, screen);
    }

    // Darkens a picture of `monitor` where the shadow of a window filling `frame` falls (for the
    // pictures Lanes makes of other desktops, as the screen would show them).
    public static unsafe void DrawInto(Pixels p, RECT monitor, RECT frame, double scale)
    {
        var look = Look(scale);
        var f = new PixelRect(frame.Left, frame.Top, frame.Width, frame.Height);
        foreach (var piece in look.Pieces(f))
        {
            int l = Math.Max(piece.X, monitor.Left), t = Math.Max(piece.Y, monitor.Top);
            int r = Math.Min(piece.Right, monitor.Right), b = Math.Min(piece.Bottom, monitor.Bottom);
            if (r <= l || b <= t) continue;
            var part = new PixelRect(l, t, r - l, b - t);
            var shade = new uint[part.W * part.H];
            look.Draw(shade, part.W, part, f);
            uint* px = (uint*)p.Data;
            for (int y = 0; y < part.H; y++)
            {
                uint* row = px + (long)(t - monitor.Top + y) * p.Width + (l - monitor.Left);
                for (int x = 0; x < part.W; x++)
                {
                    uint keep = 255 - (shade[y * part.W + x] >> 24), c = row[x];
                    if (keep == 255) continue;
                    row[x] = 0xFF000000 | ((c >> 16 & 0xFF) * keep / 255) << 16 | ((c >> 8 & 0xFF) * keep / 255) << 8 | (c & 0xFF) * keep / 255;
                }
            }
        }
    }

    delegate void WinEventProc(IntPtr hook, uint ev, IntPtr hwnd, int obj, int child, uint thread, uint time);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct WNDCLASSEX
    {
        public int Size, Style;
        public IntPtr WndProc;
        public int ClassExtra, WindowExtra;
        public IntPtr Instance, Icon, Cursor, Background;
        public string? MenuName, ClassName;
        public IntPtr SmallIcon;
    }

    [DllImport("user32.dll")] static extern IntPtr SetWinEventHook(uint min, uint max, IntPtr module, WinEventProc proc, uint pid, uint thread, uint flags);
    [DllImport("user32.dll")] static extern bool UnhookWinEvent(IntPtr hook);
    [DllImport("user32.dll")] static extern UIntPtr SetTimer(IntPtr hwnd, UIntPtr id, uint ms, IntPtr proc);
    [DllImport("user32.dll")] static extern bool PostThreadMessage(uint thread, int msg, IntPtr wParam, IntPtr lParam);
    [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] static extern bool IsZoomed(IntPtr hwnd);
    [DllImport("user32.dll")] static extern IntPtr GetDesktopWindow();
    [DllImport("user32.dll")] static extern IntPtr GetWindow(IntPtr hwnd, uint cmd);
    [DllImport("user32.dll")] static extern IntPtr BeginDeferWindowPos(int count);
    [DllImport("user32.dll")] static extern IntPtr DeferWindowPos(IntPtr info, IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] static extern bool EndDeferWindowPos(IntPtr info);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern ushort RegisterClassEx(ref WNDCLASSEX wc);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr CreateWindowEx(int exStyle, string cls, string name, int style, int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);
    [DllImport("user32.dll")] static extern bool DestroyWindow(IntPtr hwnd);
    [DllImport("kernel32.dll", CharSet = CharSet.Ansi)] static extern IntPtr GetProcAddress(IntPtr module, string name);
}
