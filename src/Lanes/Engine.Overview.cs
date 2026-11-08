using System.Drawing;
using System.Runtime.InteropServices;
using Lanes.Core;
using Vortice.Direct2D1;
using Vortice.DirectWrite;
using static Lanes.Native;

namespace Lanes;

// The overview on a three-finger swipe up: the lanes at the top, the current desktop's windows
// spread out below. The windows are live DWM thumbnails that travel from where they are to
// their place in the spread as the fingers move. Once it is open, desktops and windows can be
// dragged with the mouse, a middle click closes either, and four fingers move to another
// desktop without leaving it.
sealed partial class Engine
{
    // The windows of one desktop as the overview shows them.
    sealed class Page : IDisposable
    {
        public Guid Desktop;
        public List<AppWindow> Windows = new();
        public List<Thumbnail> Thumbs = new();
        public List<ID2D1Bitmap1?> Icons = new();
        // Where each window is on the screen, its place in the spread, and where it is drawn
        // now (gliding to a new place after a window leaves).
        public List<Box> From = new(), To = new(), Shown = new();

        public void RemoveAt(int i)
        {
            Thumbs[i].Dispose();
            Icons[i]?.Dispose();
            Windows.RemoveAt(i); Thumbs.RemoveAt(i); Icons.RemoveAt(i);
            From.RemoveAt(i); To.RemoveAt(i); Shown.RemoveAt(i);
        }

        public void Hide()
        {
            foreach (var t in Thumbs) t.Place(0, 0, 1, 1, 0);
        }

        public void Dispose()
        {
            foreach (var t in Thumbs) t.Dispose();
            foreach (var i in Icons) i?.Dispose();
        }
    }

    sealed class Zoom
    {
        public Guid Target;
        public Box From;
        public Spring Spring = null!;
        public double Start;
        public double Z;
        // Windows has switched to the target, near the end; the picture stays a moment longer.
        public double SwitchedAt = -1;
    }

    // A window or a desktop being dragged with the mouse.
    sealed class Drag
    {
        public int Window = -1; // index in the page, or -1 for a desktop
        public Guid Desktop;
        public double X, Y;          // the pointer
        public double GrabX, GrabY;  // where on the dragged thing it was picked up, 0..1
        public StripTile? Target;    // a window over a desktop, a "+" or "New lane"
        public DeskDrop? Drop;       // a desktop over the rows
    }

    // Four fingers while the overview is open: the windows of the next desktop slide in.
    sealed class InnerSwipe
    {
        public Axis Axis;
        public SwipeTrack Track = null!;
        public Guid? Previous, Next;
        public Page? PreviousPage, NextPage;
        public double Offset;
        public Spring? Spring;
        public double SpringStart;
        public Outcome Outcome;
    }

    sealed class OverviewState
    {
        // The desktop itself (wallpaper and icons), live: it fades as the overview opens.
        public Thumbnail? Desk;
        public Page Page = null!;
        public Strip Strip = null!;
        public readonly Dictionary<string, Box> TileShown = new();
        public double P;
        public bool Tracking;
        public OverviewTrack Track = null!;
        public Spring? Spring;
        public double SpringStart;
        public Zoom? Zoom;
        public int Hover = -1;
        public StripTile? HoverTile;
        public (int X, int Y)? Press;
        public Drag? Drag;
        public InnerSwipe? Swipe;
        public bool Moving; // tiles or windows still gliding to new places
        public double LastStep;
        // A desktop picked with the mouse: its lane slides it under the ring first, then it zooms in.
        public Guid? Going;
        public double GoingAt;
        public Task? GoingWallpaper;
        public Box? RingShown;
        public bool Open => !Tracking && Spring == null && Zoom == null && P > 0.99;
        public bool Idle => Open && Drag == null && Swipe == null && Going == null;
    }

    void BeginOverview()
    {
        if (_overview is { } open)
        {
            if (open.Zoom != null || open.Swipe != null || open.Drag != null || open.Going != null) { _ignoring = true; return; }
            open.Tracking = true;
            open.Spring = null;
            open.Press = null;
            _gfx.Frames.Clear();
            open.Track = new OverviewTrack(open.P, _tuning);
            return;
        }
        if (_switch != null) { _ignoring = true; return; }
        _current = Desktops.Current;
        Remember(_current, TakeTouchCapture());
        RefreshTitles();
        UseArea(FullScreen(_current));
        var o = new OverviewState { Tracking = true, Track = new OverviewTrack(0, _tuning), LastStep = Now };
        Relayout(o, snap: true);
        // Registered first, so the windows are drawn over it.
        if (FindWindow("Progman", null) is var progman && progman != IntPtr.Zero) o.Desk = new Thumbnail(_overlay.Handle, progman, _overlay.Area);
        o.Page = MakePage(_current, o.Strip);
        _overview = o;
        _gfx.Frames.Clear();
        _overlay.Place();
        RenderOverview();
        _overlay.Show();
        Log.Write($"overview: {o.Page.Windows.Count} windows; {CaptureNote}");
    }

    static string Key(StripTile t) => t.Kind == TileKind.Desktop ? t.Desktop.ToString() : $"{t.Kind}{t.Lane}";

    // Lays the rows out again after the lanes changed; tiles glide to their new places unless `snap`.
    void Relayout(OverviewState o, bool snap = false)
    {
        double spreadTop = o.Strip?.SpreadTop ?? double.NaN;
        o.Strip = Strip.Layout(_lanes, o.Going ?? _current, _gfx.Width, _gfx.Height, _overlay.Scale, dragging: o.Drag != null);
        if (snap) o.TileShown.Clear();
        foreach (var t in o.Strip.Tiles) o.TileShown.TryAdd(Key(t), t.Rect);
        foreach (var t in o.Strip.Tiles)
            if (t.Kind == TileKind.Desktop) TilePicture(t.Desktop, (float)t.Rect.W, (float)t.Rect.H);
        // More or fewer lanes move where the windows may go: they glide there too.
        if (o.Page != null && Math.Abs(o.Strip.SpreadTop - spreadTop) > 0.5) Respread(o.Page, o.Strip, o);
        o.Moving = true;
    }

    Box SpreadArea(Strip strip)
    {
        float s = _overlay.Scale;
        return new Box(72 * s, strip.SpreadTop, _gfx.Width - 144 * s, _gfx.Height - strip.SpreadTop - 20 * s);
    }

    Page MakePage(Guid desktop, Strip strip)
    {
        var area = _overlay.Area;
        float s = _overlay.Scale;
        var page = new Page { Desktop = desktop };
        page.Windows = AppWindows.OnScreen(area, _overlay.Handle, desktop == _current ? null : desktop);
        page.From = page.Windows.Select(w => new Box(w.Frame.Left - area.Left, w.Frame.Top - area.Top, w.Frame.Width, w.Frame.Height)).ToList();
        page.To = Spread.Layout(page.From, SpreadArea(strip), 32 * s, 52 * s, 200 * s).ToList();
        page.Shown = page.To.ToList();
        foreach (var w in page.Windows)
        {
            page.Thumbs.Add(new Thumbnail(_overlay.Handle, w.Handle));
            var icon = AppWindows.Icon(w.Handle, (int)(16 * s));
            page.Icons.Add(icon == null ? null : _gfx.Bitmap(icon));
            icon?.Dispose();
        }
        return page;
    }

    // The windows left on the page take the room of one that went.
    void Respread(Page page, Strip strip, OverviewState? o = null)
    {
        float s = _overlay.Scale;
        page.To = Spread.Layout(page.From, SpreadArea(strip), 32 * s, 52 * s, 200 * s).ToList();
        (o ?? _overview!).Moving = true;
    }

    void MoveOverview(double dy)
    {
        var o = _overview!;
        if (!o.Tracking) return;
        o.P = o.Track.ProgressFor(dy);
    }

    void EndOverview(double vy)
    {
        var o = _overview!;
        if (!o.Tracking) return;
        double speed = o.Track.SpeedFor(vy);
        bool open = o.Track.Release(o.P, speed);
        Log.Write($"overview release at {o.P:0.00}, {speed:0.0}/s: {(open ? "open" : "closed")}{Reported()}");
        SettleOverview(open ? 1 : 0, speed);
    }

    void SettleOverview(double target, double speed)
    {
        var o = _overview!;
        if (o.Open) _gfx.Frames.Clear();
        o.Tracking = false;
        o.Press = null;
        o.Spring = new Spring(o.P, speed, target, _tuning.OverviewResponse);
        o.SpringStart = Now;
    }

    void CloseOverview()
    {
        if (_overview is { Open: true, Swipe: null, Going: null }) SettleOverview(0, 0);
    }

    void StepOverview(double now)
    {
        var o = _overview!;
        Glide(o, now);
        if (o.Swipe != null) StepInnerSwipe(now);
        if (o.Going is Guid going && o.Zoom == null)
        {
            // Zooms in once its lane has nearly slid into place (and a new desktop's wallpaper is known).
            var to = o.Strip.Tiles.FirstOrDefault(t => t.Desktop == going && t.Kind == TileKind.Desktop);
            var at = to == null ? (Box?)null : o.TileShown.GetValueOrDefault(Key(to), to.Rect);
            bool slid = at is { } b && Math.Abs(b.X - to!.Rect.X) + Math.Abs(b.Y - to.Rect.Y) < 3 * _overlay.Scale;
            if ((slid && o.GoingWallpaper?.IsCompleted != false) || now - o.GoingAt > 0.6) ZoomInto(going, at ?? new Box(0, 0, _gfx.Width, _gfx.Height));
            return;
        }
        if (o.Zoom is { } z)
        {
            double t = now - z.Start;
            z.Z = z.Spring.At(t);
            if (z.SwitchedAt < 0 && z.Z > 0.985) SwitchForZoom(z);
            if (z.Spring.Done(t, 0.002) && now - z.SwitchedAt >= 0.2) FinishZoom();
            return;
        }
        if (o.Spring == null) return;
        double st = now - o.SpringStart;
        o.P = o.Spring.At(st);
        if (!o.Spring.Done(st, 0.002)) return;
        o.P = o.Spring.Target;
        o.Spring = null;
        if (o.P < 0.5) CloseOverviewNow();
        else
        {
            Hook(true);
            _redraw = true;
            Log.Write($"overview opened: {_gfx.FrameStats()}");
        }
    }

    // Tiles and windows that have a new place move there quickly instead of jumping.
    static void Glide(OverviewState o, double now)
    {
        double dt = Math.Clamp(now - o.LastStep, 0, 0.1);
        o.LastStep = now;
        if (!o.Moving) return;
        double k = 1 - Math.Exp(-dt / 0.055);
        bool moving = false;
        foreach (var t in o.Strip.Tiles)
            o.TileShown[Key(t)] = Toward(o.TileShown.TryGetValue(Key(t), out var b) ? b : t.Rect, t.Rect, k, ref moving);
        if (o.Going is Guid g && o.RingShown is { } ring && o.Strip.Tiles.FirstOrDefault(t => t.Desktop == g && t.Kind == TileKind.Desktop) is { } goal)
            o.RingShown = Toward(ring, goal.Rect, k, ref moving);
        for (int i = 0; i < o.Page.To.Count; i++)
            o.Page.Shown[i] = Toward(o.Page.Shown[i], o.Page.To[i], k, ref moving);
        o.Moving = moving;
    }

    static Box Toward(Box from, Box to, double k, ref bool moving)
    {
        if (Math.Abs(from.X - to.X) < 0.5 && Math.Abs(from.Y - to.Y) < 0.5 && Math.Abs(from.W - to.W) < 0.5 && Math.Abs(from.H - to.H) < 0.5) return to;
        moving = true;
        return from.Lerp(to, k);
    }

    void CloseOverviewNow()
    {
        var o = _overview;
        if (o == null) return;
        _overview = null;
        _overlay.Hide();
        Hook(false);
        Log.Write($"overview drawn: {_gfx.FrameStats()}");
        o.Desk?.Dispose();
        o.Page.Dispose();
        o.Swipe?.PreviousPage?.Dispose();
        o.Swipe?.NextPage?.Dispose();
    }

    void ZoomInto(Guid target, Box from)
    {
        var o = _overview!;
        _gfx.Frames.Clear();
        Hook(false);
        o.Zoom = new Zoom { Target = target, From = from, Spring = new Spring(0, 0, 1, _tuning.OverviewResponse), Start = Now };
    }

    // As a swipe does: Windows switches as the picture comes to rest, its pill comes up over
    // Lanes' copy, and the new desktop has a moment to draw before the picture goes.
    void SwitchForZoom(Zoom z)
    {
        z.SwitchedAt = Now;
        if (z.Target == _current) return;
        if (!Desktops.Switch(z.Target)) Log.Write("switch failed");
        _overlay.Place();
        _lastSwitchAt = Now;
        _current = z.Target;
        _lanes.Visited(z.Target);
    }

    void FinishZoom()
    {
        var z = _overview!.Zoom!;
        z.Z = 1;
        RenderOverview();
        Settings.SaveLanes(_lanes);
        CloseOverviewNow();
    }

    // Clicked: the desktop's lane slides it under the ring (the ring moves to its lane), then it zooms in.
    void GoTo(OverviewState o, Guid target, Task? wallpaper = null)
    {
        var ring = o.Strip.Tiles.FirstOrDefault(t => t.Desktop == _current && t.Kind == TileKind.Desktop);
        o.RingShown = ring == null ? null : o.TileShown.GetValueOrDefault(Key(ring), ring.Rect);
        o.Going = target;
        o.GoingAt = Now;
        o.GoingWallpaper = wallpaper;
        o.Hover = -1;
        o.HoverTile = null;
        Relayout(o);
        _gfx.Frames.Clear();
    }

    // The lanes changed: Windows' order follows them, and they are saved.
    void LanesChanged()
    {
        SyncOrder();
        _lanes.Reconcile(Desktops.All(), _current);
        Settings.SaveLanes(_lanes);
        RefreshTitles();
    }

    // A new desktop at the end of a lane, or in a new lane inserted at `lane`.
    Guid? NewDesktop(int lane, bool newLane)
    {
        var made = Desktops.Create();
        if (made == Guid.Empty) return null;
        if (newLane) _lanes.InsertLane(lane);
        _lanes.Add(made, lane);
        _lanes.Made.Add(made);
        LanesChanged();
        Log.Write(newLane ? "new lane with a new desktop" : "new desktop");
        return made;
    }

    // Windows gives a new desktop a wallpaper of its own a moment after making it; waits for
    // that (half a second at most) and makes Lanes' copy of it.
    Task OwnWallpaper(Guid desktop)
    {
        var monitor = _overlay.Monitor;
        return Task.Run(async () =>
        {
            for (int i = 0; i < 10 && Capture.OwnWallpaperPath(desktop) == null; i++) await Task.Delay(50);
            MakeWallpaper(desktop, monitor);
            Post(() => WallpaperChanged(desktop));
        });
    }

    // ---------- four fingers inside the overview ----------

    void BeginInnerSwipe(Axis axis)
    {
        var o = _overview!;
        if (o.Swipe != null) FinishInnerSwipe();
        long t0 = Environment.TickCount64;
        o.Press = null;
        o.Hover = -1;
        o.HoverTile = null;
        var sw = new InnerSwipe
        {
            Axis = axis,
            Previous = _lanes.Neighbor(_current, axis == Axis.Horizontal ? Direction.Left : Direction.Up),
            Next = _lanes.Neighbor(_current, axis == Axis.Horizontal ? Direction.Right : Direction.Down),
        };
        double page = (axis == Axis.Horizontal ? _gfx.Width : _gfx.Height) + GapDip * _overlay.Scale;
        sw.Track = new SwipeTrack(page, sw.Previous != null, sw.Next != null, _tuning);
        if (sw.Previous is Guid p) sw.PreviousPage = MakePage(p, o.Strip);
        if (sw.Next is Guid n) sw.NextPage = MakePage(n, o.Strip);
        o.Swipe = sw;
        _gfx.Frames.Clear();
        Log.Write($"overview swipe {axis}: previous {(sw.Previous is Guid a ? Title(a) : "-")}, next {(sw.Next is Guid b ? Title(b) : "-")}; windows ready in {Environment.TickCount64 - t0} ms");
    }

    void MoveInnerSwipe(double dx, double dy)
    {
        var sw = _overview!.Swipe!;
        if (sw.Spring != null) return;
        sw.Offset = sw.Track.OffsetFor(Along(dx, dy, sw.Axis));
    }

    void EndInnerSwipe(double vx, double vy)
    {
        var sw = _overview!.Swipe!;
        double speed = sw.Track.SpeedFor(Along(vx, vy, sw.Axis));
        var (outcome, target) = sw.Track.Release(sw.Offset, speed);
        sw.Outcome = outcome;
        sw.Spring = new Spring(sw.Offset, speed, target, _tuning.SwitchResponse);
        sw.SpringStart = Now;
        Log.Write($"overview swipe release at {sw.Offset / sw.Track.Page:0.00} screen, {speed / sw.Track.Page:0.00} screens/s: {outcome}{Reported()}");
    }

    void StepInnerSwipe(double now)
    {
        var sw = _overview!.Swipe!;
        if (sw.Spring == null) return;
        double t = now - sw.SpringStart;
        sw.Offset = sw.Spring.At(t);
        if (!sw.Spring.Done(t)) return;
        sw.Offset = sw.Spring.Target;
        FinishInnerSwipe();
    }

    // The swipe has landed: Windows switches underneath and the overview stays.
    void FinishInnerSwipe()
    {
        var o = _overview!;
        var sw = o.Swipe!;
        o.Swipe = null;
        Guid? to = sw.Outcome switch { Outcome.Next => sw.Next, Outcome.Previous => sw.Previous, _ => null };
        Page? arrived = sw.Outcome switch { Outcome.Next => sw.NextPage, Outcome.Previous => sw.PreviousPage, _ => null };
        foreach (var p in new[] { sw.PreviousPage, sw.NextPage })
            if (p != null && p != arrived) p.Dispose();
        if (to is Guid target && arrived != null)
        {
            if (!Desktops.Switch(target)) Log.Write("switch failed");
            _overlay.Place();
            _current = target;
            _lanes.Visited(target);
            Settings.SaveLanes(_lanes);
            o.Page.Dispose();
            o.Page = arrived;
            Relayout(o, snap: true);
            RetakeSoon();
        }
        _redraw = true;
        Log.Write($"overview swipe drawn: {_gfx.FrameStats()}");
    }

    // ---------- pointer and keys ----------

    void OnLeftDown(int x, int y)
    {
        if (_overview is { Idle: true } o) o.Press = (x, y);
    }

    void OnMouseMove(int x, int y)
    {
        var o = _overview;
        if (o is not { Open: true } || o.Swipe != null) return;
        if (o.Drag is { } d)
        {
            d.X = x;
            d.Y = y;
            Aim(o, d);
            _redraw = true;
            return;
        }
        if (o.Press is var (px, py) && Math.Abs(x - px) + Math.Abs(y - py) > 8 * _overlay.Scale)
        {
            o.Press = null;
            StartDrag(o, px, py);
            if (o.Drag is { } nd)
            {
                nd.X = x;
                nd.Y = y;
                Aim(o, nd);
            }
            _redraw = true;
            return;
        }
        int hover = HitWindow(o, x, y);
        var tile = hover >= 0 ? null : o.Strip.Hit(x, y);
        if (hover != o.Hover || tile != o.HoverTile)
        {
            o.Hover = hover;
            o.HoverTile = tile;
            _redraw = true;
        }
    }

    void StartDrag(OverviewState o, int x, int y)
    {
        int w = HitWindow(o, x, y);
        if (w >= 0)
        {
            var b = o.Page.Shown[w];
            o.Drag = new Drag { Window = w, GrabX = (x - b.X) / b.W, GrabY = (y - b.Y) / b.H };
            // Its picture goes in front of the others.
            o.Page.Thumbs[w].Dispose();
            o.Page.Thumbs[w] = new Thumbnail(_overlay.Handle, o.Page.Windows[w].Handle);
        }
        else if (o.Strip.Hit(x, y) is { Kind: TileKind.Desktop } t)
            o.Drag = new Drag { Desktop = t.Desktop, GrabX = (x - t.Rect.X) / t.Rect.W, GrabY = (y - t.Rect.Y) / t.Rect.H };
        else return;
        o.Hover = -1;
        o.HoverTile = null;
        // A "New lane" opens above the rows while something is dragged.
        Relayout(o);
    }

    void Aim(OverviewState o, Drag d)
    {
        if (d.Window >= 0)
        {
            var t = o.Strip.Hit(d.X, d.Y);
            d.Target = t is { Kind: TileKind.Desktop } && t.Desktop == _current ? null : t;
        }
        else d.Drop = o.Strip.DropFor(d.X, d.Y, d.Desktop, _lanes);
    }

    void OnLeftUp(int x, int y)
    {
        var o = _overview;
        if (o == null) return;
        if (o.Drag is { } d)
        {
            Drop(o, d);
            return;
        }
        if (o.Press == null || !o.Idle) return; // pressed before the overview was open
        o.Press = null;
        Click(o, x, y);
    }

    void Click(OverviewState o, int x, int y)
    {
        int w = HitWindow(o, x, y);
        if (w >= 0)
        {
            var hwnd = o.Page.Windows[w].Handle;
            AppWindows.Activate(hwnd);
            // Its picture goes in front of the others, as the window itself just did.
            o.Page.Thumbs[w].Dispose();
            o.Page.Thumbs[w] = new Thumbnail(_overlay.Handle, hwnd);
            CloseOverview();
            return;
        }
        switch (o.Strip.Hit(x, y))
        {
            case { Kind: TileKind.Desktop } t:
                if (t.Desktop == _current) CloseOverview();
                else GoTo(o, t.Desktop);
                return;
            case { } t:
                if (NewDesktop(t.Lane, t.Kind == TileKind.NewLane) is Guid made)
                {
                    // The new desktop grows out of the "+" (or "New lane") it was made with.
                    o.TileShown[made.ToString()] = t.Rect with { W = Math.Min(t.Rect.W, o.Strip.TileW), H = o.Strip.TileH };
                    GoTo(o, made, OwnWallpaper(made));
                }
                return;
        }
        CloseOverview();
    }

    void Drop(OverviewState o, Drag d)
    {
        o.Drag = null;
        if (d.Window >= 0)
        {
            var hwnd = o.Page.Windows[d.Window].Handle;
            Guid? to = d.Target switch
            {
                { Kind: TileKind.Desktop } t => t.Desktop,
                { } t => NewDesktop(t.Lane, t.Kind == TileKind.NewLane),
                _ => null,
            };
            if (to is Guid target)
            {
                Desktops.MoveWindow(hwnd, target);
                Log.Write($"window moved to desktop {Desktops.IndexOf(target) + 1}");
                o.Page.RemoveAt(d.Window);
                Respread(o.Page, o.Strip);
                RetakeSoon();
            }
            else
            {
                // Back to its place from where it was let go.
                o.Page.Shown[d.Window] = DraggedWindowBox(o, d);
                o.Moving = true;
            }
        }
        else
        {
            if (d.Drop is { } drop)
            {
                if (drop.NewLane) _lanes.InsertLane(drop.Lane);
                _lanes.Add(d.Desktop, drop.Lane, drop.NewLane ? null : drop.Index);
                LanesChanged();
                Log.Write(drop.NewLane ? "desktop moved to a new lane" : "desktop moved");
            }
            o.TileShown[d.Desktop.ToString()] = DraggedTileBox(o, d);
        }
        Relayout(o);
        _redraw = true;
    }

    void OnMiddleUp(int x, int y)
    {
        var o = _overview;
        if (o is not { Idle: true }) return;
        int w = HitWindow(o, x, y);
        if (w >= 0)
        {
            // As in Task View: the window is asked to close (it may still ask to save first).
            PostMessage(o.Page.Windows[w].Handle, WM_SYSCOMMAND, (IntPtr)SC_CLOSE, IntPtr.Zero);
            Log.Write("window closed");
            RetakeSoon();
            o.Page.RemoveAt(w);
            o.Hover = -1;
            Respread(o.Page, o.Strip);
            _redraw = true;
            return;
        }
        if (o.Strip.Hit(x, y) is { Kind: TileKind.Desktop } t) CloseDesktop(o, t.Desktop);
    }

    // Closes a desktop; its windows go to the one next to it, as when Windows closes a desktop.
    void CloseDesktop(OverviewState o, Guid desktop)
    {
        if (Desktops.Count <= 1) return;
        Guid? fallback = _lanes.Neighbor(desktop, Direction.Left) ?? _lanes.Neighbor(desktop, Direction.Right)
            ?? _lanes.Neighbor(desktop, Direction.Up) ?? _lanes.Neighbor(desktop, Direction.Down);
        if (fallback is not Guid to) return;
        bool here = desktop == _current;
        Desktops.Remove(desktop, to);
        Log.Write($"closed a desktop; its windows went to desktop {Desktops.IndexOf(to) + 1}");
        if (here)
        {
            _current = Desktops.Current is var now && now != desktop ? now : to;
            _overlay.Place();
        }
        LanesChanged();
        Relayout(o);
        if (here || to == _current)
        {
            var old = o.Page;
            o.Page = MakePage(_current, o.Strip);
            old.Dispose();
        }
        o.HoverTile = null;
        _redraw = true;
        if (here || to == _current) RetakeSoon();
    }

    static int HitWindow(OverviewState o, int x, int y)
    {
        for (int i = o.Page.Shown.Count - 1; i >= 0; i--)
        {
            var b = o.Page.Shown[i];
            if (x >= b.X && x < b.Right && y >= b.Y && y < b.Bottom) return i;
        }
        return -1;
    }

    // A dragged window gets smaller over the rows, so the desktop it would go to stays visible.
    Box DraggedWindowBox(OverviewState o, Drag d)
    {
        var b = o.Page.Shown[d.Window];
        double near = Math.Clamp((o.Strip.SpreadTop - d.Y) / (90 * _overlay.Scale), 0, 1);
        double w = b.W + (Math.Min(b.W, o.Strip.TileW * 0.7) - b.W) * near, h = w * b.H / b.W;
        return new Box(d.X - d.GrabX * w, d.Y - d.GrabY * h, w, h);
    }

    Box DraggedTileBox(OverviewState o, Drag d) =>
        new(d.X - d.GrabX * o.Strip.TileW, d.Y - d.GrabY * o.Strip.TileH, o.Strip.TileW, o.Strip.TileH);

    IntPtr _hook;
    HookProc? _hookProc;

    void Hook(bool on)
    {
        if (on && _hook == IntPtr.Zero)
        {
            _hookProc = KeyHook;
            _hook = SetWindowsHookEx(13 /* WH_KEYBOARD_LL */, _hookProc, GetModuleHandle(null), 0);
        }
        else if (!on && _hook != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_hook);
            _hook = IntPtr.Zero;
        }
    }

    IntPtr KeyHook(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0 && wParam == (IntPtr)WM_KEYDOWN && Marshal.ReadInt32(lParam) == 0x1B)
        {
            if (_overview is { Drag: { } d } o)
            {
                // Esc while dragging puts the thing back.
                d.Target = null;
                d.Drop = null;
                Drop(o, d);
            }
            else CloseOverview();
            return 1;
        }
        return CallNextHookEx(_hook, code, wParam, lParam);
    }

    // For the demo run: where the overview's tiles and windows are on the screen now.
    public Task<(List<(TileKind Kind, int Lane, Guid Desktop, Rectangle Rect)> Tiles, List<(string Title, Rectangle Rect)> Windows)> OverviewLayout() => Ask(() =>
    {
        var tiles = new List<(TileKind, int, Guid, Rectangle)>();
        var windows = new List<(string, Rectangle)>();
        if (_overview is not { } o) return (tiles, windows);
        var area = _overlay.Area;
        Rectangle On(Box b) => new(area.Left + (int)b.X, area.Top + (int)b.Y, (int)b.W, (int)b.H);
        foreach (var t in o.Strip.Tiles) tiles.Add((t.Kind, t.Lane, t.Desktop, On(t.Rect)));
        for (int i = 0; i < o.Page.Windows.Count; i++) windows.Add((o.Page.Windows[i].Title, On(o.Page.Shown[i])));
        return (tiles, windows);
    });

    // ---------- drawing ----------

    void RenderOverview()
    {
        var o = _overview!;
        var g = _gfx;
        float s = _overlay.Scale;
        float p = (float)o.P, pc = Math.Clamp(p, 0, 1);
        float z = (float)(o.Zoom?.Z ?? 0), keep = 1 - Math.Clamp(z, 0, 1);
        var sw = o.Swipe;
        // How far a four-finger swipe has gone, -1..1; negative toward the next desktop.
        float f = sw == null ? 0 : (float)Math.Clamp(sw.Offset / sw.Track.Page, -1, 1);
        Guid? toward = f < 0 ? sw?.Next : f > 0 ? sw?.Previous : null;
        Page? other = f < 0 ? sw?.NextPage : f > 0 ? sw?.PreviousPage : null;
        bool vertical = sw?.Axis == Axis.Vertical;
        g.Begin(Gfx.Color(0xFF000000));

        if (Wallpaper(_current) is var (sharp, blurred))
        {
            g.Image(sharp, 0, 0, g.Width, g.Height);
            g.Image(blurred, 0, 0, g.Width, g.Height, pc);
        }
        if (toward is Guid tw && Wallpaper(tw) is var (_, theirs)) g.Image(theirs, 0, 0, g.Width, g.Height, pc * Math.Abs(f));
        g.Rect(0, 0, g.Width, g.Height, A(0xFF000000, 0.55f * pc));
        // The icons on the desktop are there as the fingers start, and fade as it opens.
        o.Desk?.Place(0, 0, g.Width, g.Height, 1 - Math.Clamp(p / 0.35f, 0, 1));

        // Where each window is at this moment.
        var page = o.Page;
        var boxes = new Box[page.Thumbs.Count];
        double highest = double.MaxValue;
        for (int i = 0; i < boxes.Length; i++)
        {
            boxes[i] = page.From[i].Lerp(page.Shown[i], Math.Clamp(p, -0.05, 1.08));
            highest = Math.Min(highest, boxes[i].Y);
        }

        // Lane rows. They come down from above the screen just ahead of the windows, so they
        // never overlap them (as the Spaces bar does in Mission Control).
        float sp = Math.Clamp(p / 0.6f, 0, 1), sq = 1 - MathF.Pow(1 - sp, 3);
        float a = sq * keep, dy = (1 - sq) * -40 * s;
        if (boxes.Length > 0) dy = Math.Min(dy, Math.Min(0, (float)highest - 30 * s - (float)o.Strip.Bottom));
        if (a > 0.002f) DrawStrip(o, a, dy, f, toward, p);

        // The windows of this desktop, and of the one a four-finger swipe brings in.
        float la = Math.Clamp((p - 0.7f) / 0.3f, 0, 1) * keep;
        double ox = sw == null || vertical ? 0 : sw.Offset, oy = vertical ? sw!.Offset : 0;
        float clip = vertical ? (float)o.Strip.Bottom + dy : float.NegativeInfinity;
        // A window going up under the lane rows fades as it goes, instead of being cut off.
        float Fade(Box b) => vertical ? 1 - Math.Clamp((clip + 40 * s - (float)b.Y) / Math.Max(1, (float)b.H * 0.6f), 0, 1) : 1;
        if (vertical) g.Clip(0, clip, g.Width, g.Height - clip);
        for (int i = 0; i < boxes.Length; i++)
        {
            if (o.Drag?.Window == i) continue;
            var b = boxes[i] with { X = boxes[i].X + ox, Y = boxes[i].Y + oy };
            page.Thumbs[i].Place((float)b.X, (float)b.Y, (float)b.W, (float)b.H, keep * Fade(b), clip);
            if (la > 0.002f) WindowTitle(page, i, b, la * Fade(b));
            if (o.Hover == i && o.Idle)
                g.RoundRectOutline((float)b.X - 5 * s, (float)b.Y - 5 * s, (float)b.W + 10 * s, (float)b.H + 10 * s, 11 * s, 0xD9FFFFFF, 2 * s);
        }
        foreach (var unused in new[] { sw?.PreviousPage, sw?.NextPage })
            if (unused != null && unused != other) unused.Hide();
        if (other != null)
        {
            double step = sw!.Track.Page * (f < 0 ? 1 : -1);
            for (int i = 0; i < other.Thumbs.Count; i++)
            {
                var b = other.To[i] with { X = other.To[i].X + ox + (vertical ? 0 : step), Y = other.To[i].Y + oy + (vertical ? step : 0) };
                other.Thumbs[i].Place((float)b.X, (float)b.Y, (float)b.W, (float)b.H, keep * Fade(b), clip);
                WindowTitle(other, i, b, la * Fade(b));
            }
        }
        if (vertical) g.Unclip();

        if (o.Drag is { Window: >= 0 } dw)
        {
            var b = DraggedWindowBox(o, dw);
            page.Thumbs[dw.Window].Place((float)b.X, (float)b.Y, (float)b.W, (float)b.H);
        }

        if (o.Zoom is { } zoom && Picture(zoom.Target) is { } target)
        {
            var b = zoom.From.Lerp(new Box(0, 0, g.Width, g.Height), Math.Clamp(z, 0, 1));
            g.RoundImage(target, (float)b.X, (float)b.Y, (float)b.W, (float)b.H, 8 * s * keep, 1);
            // As after a swipe, the desktop's pill: Windows' own comes up over it once it switched.
            DesktopPill(zoom.Target, 0, Math.Clamp((z - 0.75f) / 0.25f, 0, 1));
        }
        g.End();
    }

    void DrawStrip(OverviewState o, float a, float dy, float f, Guid? toward, float p)
    {
        var g = _gfx;
        float s = _overlay.Scale;
        var capFont = g.Font(14 * s, FontWeight.Normal, centred: true);
        var newLaneFont = g.Font(13 * s);
        var strip = o.Strip;
        var drag = o.Drag;
        int lane = _lanes.Locate(_current)?.Lane ?? -1;
        // A sideways four-finger swipe slides your lane through the column.
        float shift = o.Swipe?.Axis == Axis.Horizontal ? f * (float)(strip.TileW + strip.Gap) : 0;
        // The column of current desktops as one object: what is above and below you.
        g.RoundRect((float)strip.Column.X, (float)strip.Column.Y + dy, (float)strip.Column.W, (float)strip.Column.H, 12 * s, A(0x0DFFFFFF, a));
        Box? ring = null, ringTo = null;
        foreach (var t in strip.Tiles)
        {
            var r = o.TileShown.GetValueOrDefault(Key(t), t.Rect);
            float x = (float)r.X + (t.Lane == lane && t.Kind != TileKind.NewLane ? shift : 0), y = (float)r.Y + dy, w = (float)r.W, h = (float)r.H;
            bool targeted = drag?.Target == t || drag?.Drop is { NewLane: true } nd && t.Kind == TileKind.NewLane && t.Lane == nd.Lane;
            bool hover = o.HoverTile == t && o.Idle;
            if (t.Kind != TileKind.Desktop)
            {
                bool newLane = t.Kind == TileKind.NewLane;
                // The "New lane" under the rows comes in last, like the window names.
                float pa = newLane && drag == null ? a * Math.Clamp((p - 0.7f) / 0.3f, 0, 1) : a;
                if (pa <= 0.002f) continue;
                g.RoundRect(x, y, w, h, 8 * s, A(targeted ? 0x334CC2FFu : hover ? 0x1AFFFFFFu : 0x0AFFFFFFu, pa));
                g.RoundRectOutline(x + 0.5f, y + 0.5f, w - 1, h - 1, 8 * s, targeted ? A(0xFF4CC2FF, pa) : A(hover ? 0x59FFFFFFu : 0x29FFFFFFu, pa), targeted ? 2 * s : 1);
                float label = newLane ? g.TextWidth("New lane", newLaneFont) + 10 * s : 0;
                float cx = x + (w - label) / 2, cy = y + h / 2, arm = (newLane ? 5 : 9) * s;
                uint ink = A(hover || targeted ? 0xE6FFFFFFu : 0x80FFFFFFu, pa);
                g.Rect(cx - arm, cy - 0.8f * s, 2 * arm, 1.6f * s, ink);
                g.Rect(cx - 0.8f * s, cy - arm, 1.6f * s, 2 * arm, ink);
                if (newLane) g.Text("New lane", newLaneFont, cx + arm + 10 * s, y, label, h, A(0xA6FFFFFF, pa));
                else if (hover || targeted)
                {
                    float tw = g.TextWidth("New desktop", capFont);
                    g.Text("New desktop", capFont, x + (w - tw) / 2, y + h + 6 * s, tw, 18 * s, A(0xFFE6E6E6, pa));
                }
                continue;
            }
            if (t.Desktop == _current) ring = new Box(x, y, w, h);
            if (t.Desktop == toward) ringTo = new Box(x, y, w, h);
            // A desktop being dragged leaves a faint place behind.
            float ta = drag != null && drag.Window < 0 && drag.Desktop == t.Desktop ? a * 0.3f : a;
            g.Shadow(x, y, w, h, 8 * s, 18 * s, 6 * s, 0.4f * ta);
            DrawTilePicture(t.Desktop, x, y, w, h, ta);
            g.RoundRectOutline(x + 0.5f, y + 0.5f, w - 1, h - 1, 8 * s, A(0x1FFFFFFF, ta), 1);
            if (targeted) g.RoundRectOutline(x - 4 * s, y - 4 * s, w + 8 * s, h + 8 * s, 11 * s, A(0xFFFFFFFF, a), 2.5f * s);
            else if (hover && t.Desktop != _current) g.RoundRectOutline(x - 4 * s, y - 4 * s, w + 8 * s, h + 8 * s, 11 * s, A(0xD9FFFFFF, a), 2 * s);
            string title = Title(t.Desktop);
            float cw = Math.Min(g.TextWidth(title, capFont), w + 16 * s);
            g.Text(title, capFont, x + (w - cw) / 2, y + h + 6 * s, cw, 18 * s, A(0xFFE6E6E6, ta));
        }

        // Where you are. A sideways swipe slides the lane under it; one up or down moves it to the next lane.
        if (o.Going != null && o.RingShown is { } going) ring = going with { Y = going.Y + dy };
        if (ring is { } rb)
        {
            if (o.Swipe?.Axis == Axis.Horizontal) rb = rb with { X = rb.X - shift };
            if (o.Swipe?.Axis == Axis.Vertical && ringTo is { } tb) rb = rb.Lerp(tb, Math.Abs(f));
            g.RoundRectOutline((float)rb.X - 4 * s, (float)rb.Y - 4 * s, (float)rb.W + 8 * s, (float)rb.H + 8 * s, 11 * s, A(0xFF4CC2FF, a), 2 * s);
        }

        if (drag is { Window: < 0 } dd)
        {
            if (dd.Drop is { NewLane: false } drop)
            {
                var m = strip.Marker(drop, dd.Desktop, s);
                g.RoundRect((float)m.X, (float)m.Y + dy, (float)m.W, (float)m.H, 2 * s, A(0xFF4CC2FF, a));
            }
            var b = DraggedTileBox(o, dd);
            float x = (float)b.X, y = (float)b.Y, w = (float)b.W, h = (float)b.H;
            g.Shadow(x, y, w, h, 8 * s, 28 * s, 10 * s, 0.55f);
            DrawTilePicture(dd.Desktop, x, y, w, h, 1);
            g.RoundRectOutline(x + 0.5f, y + 0.5f, w - 1, h - 1, 8 * s, 0x40FFFFFF, 1);
        }
    }

    void DrawTilePicture(Guid desktop, float x, float y, float w, float h, float alpha)
    {
        if (_tilePictures.TryGetValue(desktop, out var small)) _gfx.Image(small, x, y, w, h, alpha);
        else if (Picture(desktop) is { } pic) _gfx.RoundImage(pic, x, y, w, h, 8 * _overlay.Scale, alpha);
    }

    // The window's icon and name above it, once it has nearly arrived.
    void WindowTitle(Page page, int i, Box b, float la)
    {
        if (la <= 0.002f) return;
        var g = _gfx;
        float s = _overlay.Scale;
        var nameFont = g.Font(15 * s);
        float icon = 16 * s, gap = 8 * s, room = (float)b.W - 16 * s - icon - gap;
        string title = page.Windows[i].Title;
        // A title too long for the window's width gives way to the program's name.
        if (g.TextWidth(title, nameFont) > room && page.Windows[i].App.Length > 0) title = page.Windows[i].App;
        float tw = Math.Min(g.TextWidth(title, nameFont), room);
        float x = (float)(b.X + (b.W - (icon + gap + tw)) / 2), y = (float)b.Y - 28 * s;
        if (page.Icons[i] is { } ic) g.Image(ic, x, y, icon, icon, la);
        g.Text(title, nameFont, x + icon + gap, y - 2 * s, tw, 20 * s, A(0xFFFFFFFF, la));
    }

    delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] static extern IntPtr SetWindowsHookEx(int id, HookProc proc, IntPtr module, uint thread);
    [DllImport("user32.dll")] static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")] static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr FindWindow(string cls, string? title);
}
