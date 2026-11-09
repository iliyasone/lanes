using System.Drawing;
using System.Runtime.InteropServices;
using Lanes.Core;
using Vortice.Direct2D1;
using Vortice.DirectWrite;
using static Lanes.Native;

namespace Lanes;

// The overview on a three-finger swipe up, as Mission Control: the lanes at the top on a
// frosted band that slides down from above, the current desktop's windows spread out below over
// the desktop itself (sharp, with its icons). The windows are live DWM thumbnails that travel
// from where they are to their place in the spread as the fingers move. Once it is open, the
// pointer over a desktop shows that desktop's windows at once (a click, or closing the overview,
// then lands there), desktops and windows can be dragged with the mouse, a middle click closes
// either, and four fingers (or two) move to another desktop without leaving it.
sealed partial class Engine
{
    // The windows of one desktop as the overview shows them.
    sealed class Page : IDisposable
    {
        public Guid Desktop;
        public List<AppWindow> Windows = new();
        public List<Thumbnail> Thumbs = new();
        // Where each window is on the screen, its place in the spread, and where it is drawn
        // now (gliding to a new place after a window leaves).
        public List<Box> From = new(), To = new(), Shown = new();

        public void RemoveAt(int i)
        {
            Thumbs[i].Dispose();
            Windows.RemoveAt(i); Thumbs.RemoveAt(i);
            From.RemoveAt(i); To.RemoveAt(i); Shown.RemoveAt(i);
        }

        public void Hide()
        {
            foreach (var t in Thumbs) t.Place(0, 0, 1, 1, 0);
        }

        public void Dispose()
        {
            foreach (var t in Thumbs) t.Dispose();
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

    // Two fingers sideways over another lane's row: the row slides (`Shift`, from its desktop
    // `From` in the middle column).
    sealed class RowScroll
    {
        public int Lane, From;
        public double Shift;
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
        // The windows shown: of the desktop you are on, or of the one the pointer showed.
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
        public RowScroll? Scroll;
        // The desktop the pointer rests on, which shows its windows after a moment.
        public Guid? Dwell;
        public double DwellSince;
        // A desktop being renamed, and its name so far.
        public Guid? Renaming;
        public string Name = "";
        // A desktop picked with the mouse: its lane slides it under the ring first, then it zooms in.
        public Guid? Going;
        public double GoingAt;
        public Task? GoingWallpaper;
        public Box? RingShown;
        // The menu a right click on a desktop opened, and the item under the pointer.
        public Guid? MenuFor;
        public Box MenuBox;
        public int MenuItem = -1;
        // A desktop just made with the "+", lit up for a moment.
        public Guid? Added;
        public double AddedAt;
        // When the windows were last looked at again.
        public double LastLook;
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
            _overlay.Hole(null);
            open.Press = null;
            _gfx.Frames.Clear();
            open.Track = new OverviewTrack(open.P, _tuning);
            return;
        }
        if (_switch != null) { _ignoring = true; return; }
        _current = Desktops.Current;
        Remember(_current, TakeTouchCapture());
        RefreshTitles();
        UseArea();
        var o = new OverviewState { Tracking = true, Track = new OverviewTrack(0, _tuning), LastStep = Now };
        Relayout(o, snap: true);
        o.Page = NewPage(_current, o.Strip);
        _overview = o;
        _gfx.Frames.Clear();
        _overlay.Place();
        RenderOverview();
        _overlay.Show();
        _map.Hide();
        Log.Write($"overview: {o.Page.Windows.Count} windows; {CaptureNote}");
    }

    static string Key(StripTile t) => t.Kind switch
    {
        TileKind.Desktop => t.Desktop.ToString(),
        TileKind.Plus => "Plus",
        _ => $"{t.Kind}{t.Lane}",
    };

    // Lays the rows out again after the lanes changed; tiles glide to their new places unless `snap`.
    void Relayout(OverviewState o, bool snap = false)
    {
        double spreadTop = o.Strip?.SpreadTop ?? double.NaN;
        o.Strip = Strip.Layout(_lanes, o.Going ?? _current, _gfx.Width, _gfx.Height, _overlay.Scale, dragging: o.Drag != null,
            offset: WorkTop, room: WorkBottom - WorkTop, bin: o.Drag is { Window: < 0 } && Desktops.Count > 1);
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
        return new Box(72 * s, strip.SpreadTop, _gfx.Width - 144 * s, WorkBottom - strip.SpreadTop - 20 * s);
    }

    Page MakePage(Guid desktop, Strip strip)
    {
        var area = _overlay.Area;
        float s = _overlay.Scale;
        var page = new Page { Desktop = desktop };
        page.Windows = AppWindows.OnScreen(area, _overlay.Handle, desktop == _current ? null : desktop);
        page.From = page.Windows.Select(w => new Box(w.Frame.Left - area.Left, w.Frame.Top - area.Top, w.Frame.Width, w.Frame.Height)).ToList();
        page.To = Spread.Layout(page.From, SpreadArea(strip), 32 * s, 52 * s).ToList();
        page.Shown = page.To.ToList();
        foreach (var w in page.Windows) page.Thumbs.Add(new Thumbnail(_overlay.Handle, w.Handle));
        return page;
    }

    // A page, and the taskbar's place among the window pictures: over them, as on the screen,
    // except on a desktop with a full-screen window, which lies over the taskbar; opening the
    // overview there, the taskbar comes out from under the window as it shrinks.
    Page NewPage(Guid desktop, Strip strip)
    {
        bool under = FullScreen(desktop);
        if (under) RaiseTaskbar();
        var page = MakePage(desktop, strip);
        if (!under) RaiseTaskbar();
        return page;
    }

    // The windows of another desktop, shown at once in place of these.
    void ShowPage(OverviewState o, Guid desktop)
    {
        var old = o.Page;
        o.Page = NewPage(desktop, o.Strip);
        old.Dispose();
        o.Hover = -1;
        _redraw = true;
    }

    // The desktop whose windows are shown becomes the one you are on: Windows switches under the
    // overview, before it closes there or a swipe goes on from there.
    void Arrive(OverviewState o)
    {
        var d = o.Page.Desktop;
        if (d == _current) return;
        SwitchTo(d);
        _overlay.Place();
        _current = d;
        _lanes.Visited(d);
        Settings.SaveLanes(_lanes);
        RetakeSoon();
        UpdateHole(o);
        Log.Write($"overview: now on desktop {Desktops.IndexOf(d) + 1}");
    }

    // While the overview is open, the real taskbar shows through it and takes clicks (the tray,
    // the clock), except over a full-screen window, which lies over the real taskbar.
    void UpdateHole(OverviewState o) =>
        _overlay.Hole(o.Open && o.Zoom == null && !FullScreen(_current) ? TaskbarOn(_overlay.Monitor) : null);

    // ---------- two fingers over another lane ----------

    // Two fingers sideways over another lane's row slide that row, without leaving your
    // desktop: the desktop brought to the middle column is where a swipe into that lane lands.
    bool BeginRowScroll(Axis axis)
    {
        if (_overview is not { Idle: true } o || axis != Axis.Horizontal) return false;
        GetCursorPos(out var pt);
        if (o.Strip.LaneAt(pt.Y - _overlay.Area.Top) is not int lane || lane == _lanes.Locate(_current)?.Lane) return false;
        var desks = _lanes.Lanes[lane].Desktops;
        o.Scroll = new RowScroll { Lane = lane, From = Math.Max(0, desks.IndexOf(_lanes.CurrentOf(lane) ?? Guid.Empty)) };
        o.Hover = -1;
        o.HoverTile = null;
        o.Dwell = null;
        return true;
    }

    void MoveRowScroll(double dx)
    {
        var o = _overview!;
        var sc = o.Scroll!;
        double step = o.Strip.TileW + o.Strip.Gap, shift = dx * 3 * step;
        int count = _lanes.Lanes[sc.Lane].Desktops.Count;
        double most = sc.From * step, least = -(count - 1 - sc.From) * step;
        // Past the lane's ends the row gives a little.
        if (shift > most) shift = most + (shift - most) * 0.3;
        if (shift < least) shift = least + (shift - least) * 0.3;
        sc.Shift = shift;
        foreach (var t in o.Strip.Tiles.Where(t => t.Kind == TileKind.Desktop && t.Lane == sc.Lane))
            o.TileShown[Key(t)] = t.Rect with { X = t.Rect.X + shift };
    }

    void EndRowScroll(double vx)
    {
        var o = _overview!;
        var sc = o.Scroll!;
        o.Scroll = null;
        double step = o.Strip.TileW + o.Strip.Gap;
        var desks = _lanes.Lanes[sc.Lane].Desktops;
        int to = Math.Clamp((int)Math.Round(sc.From - (sc.Shift + vx * 3 * step * 0.12) / step), 0, desks.Count - 1);
        if (to != sc.From)
        {
            _lanes.Visited(desks[to]);
            Settings.SaveLanes(_lanes);
            Log.Write($"overview: lane {sc.Lane + 1} slid to desktop {Desktops.IndexOf(desks[to]) + 1}");
        }
        Relayout(o);
    }

    // Windows that opened, came up or closed while the overview is open join or leave the
    // spread; a new one comes from where it is on the screen to its place among the others.
    void LookAgain(OverviewState o)
    {
        var page = o.Page;
        var area = _overlay.Area;
        var now = AppWindows.OnScreen(area, _overlay.Handle, page.Desktop == _current ? null : page.Desktop);
        bool changed = false;
        for (int i = page.Windows.Count - 1; i >= 0; i--)
            if (!now.Any(w => w.Handle == page.Windows[i].Handle)) { page.RemoveAt(i); changed = true; }
        foreach (var w in now)
        {
            if (page.Windows.Any(x => x.Handle == w.Handle)) continue;
            var from = new Box(w.Frame.Left - area.Left, w.Frame.Top - area.Top, w.Frame.Width, w.Frame.Height);
            page.Windows.Add(w);
            page.From.Add(from); page.To.Add(from); page.Shown.Add(from);
            page.Thumbs.Add(new Thumbnail(_overlay.Handle, w.Handle));
            changed = true;
        }
        if (!changed) return;
        if (!FullScreen(page.Desktop)) RaiseTaskbar();
        o.Hover = -1;
        Respread(page, o.Strip, o);
        Log.Write($"overview: {page.Windows.Count} windows now");
    }

    // The windows left on the page take the room of one that went.
    void Respread(Page page, Strip strip, OverviewState? o = null)
    {
        float s = _overlay.Scale;
        page.To = Spread.Layout(page.From, SpreadArea(strip), 32 * s, 52 * s).ToList();
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
        _overlay.Hole(null);
        if (target < 0.5) Arrive(o);
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
        if (o.Added != null && now - o.AddedAt > 0.6) { o.Added = null; _redraw = true; }
        // Resting on a desktop for a moment shows its windows, as if you were there.
        if (o.Dwell is Guid dwell && now - o.DwellSince >= 0.7)
        {
            o.Dwell = null;
            if (o.Idle && dwell != o.Page.Desktop) ShowPage(o, dwell);
        }
        if (o.Idle && now - o.LastLook > 0.5)
        {
            o.LastLook = now;
            LookAgain(o);
        }
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
            UpdateHole(o);
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
        _overlay.Hole(null);
        _overlay.Hide();
        DropTaskbar();
        Hook(false);
        Log.Write($"overview drawn: {_gfx.FrameStats()}");
        o.Page.Dispose();
        o.Swipe?.PreviousPage?.Dispose();
        o.Swipe?.NextPage?.Dispose();
    }

    void ZoomInto(Guid target, Box from)
    {
        var o = _overview!;
        _gfx.Frames.Clear();
        Hook(false);
        _overlay.Hole(null);
        o.Zoom = new Zoom { Target = target, From = from, Spring = new Spring(0, 0, 1, _tuning.OverviewResponse), Start = Now };
    }

    // As a swipe does: Windows switches as the picture comes to rest, its pill comes up over
    // Lanes' copy, and the new desktop has a moment to draw before the picture goes.
    void SwitchForZoom(Zoom z)
    {
        z.SwitchedAt = Now;
        if (z.Target == _current) return;
        SwitchTo(z.Target);
        _overlay.Place();
        _current = z.Target;
        _lanes.Visited(z.Target);
    }

    void FinishZoom()
    {
        var z = _overview!.Zoom!;
        z.Z = 1;
        RenderOverview();
        Settings.SaveLanes(_lanes);
        // Lanes' pill, as after a swipe, shows where you are now.
        var target = z.Target;
        _pillX = _pillAside = null;
        LingerPill(p => DrawPill(p, target, null, 0, 0, 1), z.SwitchedAt);
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
        if (o.Page.Desktop != _current)
        {
            Arrive(o);
            Relayout(o);
        }
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
        double page = axis == Axis.Horizontal ? _gfx.Width : _gfx.Height;
        sw.Track = new SwipeTrack(page, sw.Previous != null, sw.Next != null, _tuning);
        if (sw.Previous is Guid p) sw.PreviousPage = MakePage(p, o.Strip);
        if (sw.Next is Guid n) sw.NextPage = MakePage(n, o.Strip);
        RaiseTaskbar();
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
            SwitchTo(target);
            _overlay.Place();
            _current = target;
            _lanes.Visited(target);
            Settings.SaveLanes(_lanes);
            o.Page.Dispose();
            o.Page = arrived;
            Relayout(o, snap: true);
            RetakeSoon();
            UpdateHole(o);
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
        if (o.MenuFor != null)
        {
            int item = MenuItemAt(o, x, y);
            if (item != o.MenuItem) { o.MenuItem = item; _redraw = true; }
            return;
        }
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
        // Resting on a desktop shows its windows after a moment (see StepOverview); they stay
        // while the pointer goes on down to them.
        if (tile is { Kind: TileKind.Desktop } && tile.Desktop != o.Page.Desktop && o.Idle)
        {
            if (o.Dwell != tile.Desktop) { o.Dwell = tile.Desktop; o.DwellSince = Now; }
        }
        else o.Dwell = null;
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
        // Places for a new lane open above and below the rows while something is dragged.
        Relayout(o);
    }

    void Aim(OverviewState o, Drag d)
    {
        var t = o.Strip.Hit(d.X, d.Y);
        if (d.Window >= 0) d.Target = t is { Kind: TileKind.Desktop } && t.Desktop == o.Page.Desktop ? null : t;
        else if (t is { Kind: TileKind.Trash })
        {
            // Over the bin: let go, and the desktop closes.
            d.Target = t;
            d.Drop = null;
        }
        else
        {
            d.Target = null;
            d.Drop = o.Strip.DropFor(d.X, d.Y, d.Desktop, _lanes);
        }
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
        // Pressed before the overview was open, or while something moves.
        if (o.Press == null || !o.Idle) { Log.Write("overview click: not taken, the overview was busy"); return; }
        o.Press = null;
        Click(o, x, y);
    }

    void Click(OverviewState o, int x, int y)
    {
        if (o.Renaming != null)
        {
            FinishRename(o, keep: true);
            return;
        }
        if (o.MenuFor is Guid menuFor)
        {
            o.MenuFor = null;
            _redraw = true;
            switch (MenuItemAt(o, x, y))
            {
                case 0:
                    o.Renaming = menuFor;
                    o.Name = Desktops.Name(menuFor);
                    Log.Write("overview: renaming a desktop");
                    break;
                case 1: CloseDesktop(o, menuFor); break;
                default: Log.Write("overview click: menu closed"); break;
            }
            return;
        }
        int w = HitWindow(o, x, y);
        Log.Write($"overview click: {HitName(o, w, x, y)}");
        if (w >= 0)
        {
            var hwnd = o.Page.Windows[w].Handle;
            Arrive(o);
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
                // Its windows are shown already (the pointer passed over it): the overview closes there.
                if (t.Desktop == o.Page.Desktop) CloseOverview();
                else GoTo(o, t.Desktop);
                return;
            case { Kind: TileKind.Plus } t:
                if (NewDesktop(t.Lane, false) is Guid made)
                {
                    // It flies out of the "+" to the end of your lane and lights up a moment; you
                    // stay where you are.
                    o.TileShown[made.ToString()] = t.Rect;
                    o.Added = made;
                    o.AddedAt = Now;
                    _ = OwnWallpaper(made);
                    Relayout(o);
                    _redraw = true;
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
        else if (d.Target is { Kind: TileKind.Trash })
        {
            CloseDesktop(o, d.Desktop);
            return;
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
        if (o == null) return;
        if (!o.Idle) { Log.Write("overview middle click: not taken, the overview was busy"); return; }
        o.MenuFor = null;
        int w = HitWindow(o, x, y);
        Log.Write($"overview middle click: {HitName(o, w, x, y)}");
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

    // A right click on a desktop opens a menu to rename it or close it, as in Task View.
    void OnRightUp(int x, int y)
    {
        var o = _overview;
        if (o == null) return;
        if (!o.Idle) { Log.Write("overview right click: not taken, the overview was busy"); return; }
        if (o.Renaming != null) FinishRename(o, keep: true);
        o.MenuFor = null;
        _redraw = true;
        int w = HitWindow(o, x, y);
        Log.Write($"overview right click: {HitName(o, w, x, y)}");
        if (w >= 0 || o.Strip.Hit(x, y) is not { Kind: TileKind.Desktop } t) return;
        float s = _overlay.Scale;
        var items = MenuItems();
        double mw = items.Max(i => _gfx.TextWidth(i, MenuFont())) + 40 * s, mh = (items.Length * MenuRow + 8) * s;
        o.MenuFor = t.Desktop;
        o.MenuBox = new Box(Math.Clamp(x, 0, _gfx.Width - mw), Math.Clamp(y, 0, _gfx.Height - mh), mw, mh);
        o.MenuItem = MenuItemAt(o, x, y);
    }

    const float MenuRow = 36;

    // The last desktop cannot be closed.
    static string[] MenuItems() => Desktops.Count > 1 ? new[] { "Rename", "Close desktop" } : new[] { "Rename" };

    int MenuItemAt(OverviewState o, int x, int y)
    {
        if (!Inside(o.MenuBox, x, y)) return -1;
        int i = (int)((y - o.MenuBox.Y - 4 * _overlay.Scale) / (MenuRow * _overlay.Scale));
        return i >= 0 && i < MenuItems().Length ? i : -1;
    }

    IDWriteTextFormat MenuFont() => _gfx.Font(14 * _overlay.Scale, FontWeight.Normal, family: "Segoe UI Variable Text");

    void DrawMenu(OverviewState o)
    {
        var g = _gfx;
        float s = _overlay.Scale;
        float x = (float)o.MenuBox.X, y = (float)o.MenuBox.Y, w = (float)o.MenuBox.W, h = (float)o.MenuBox.H, row = MenuRow * s;
        g.Shadow(x, y, w, h, 8 * s, 20 * s, 6 * s, 0.5f);
        g.RoundRect(x, y, w, h, 8 * s, 0xFF2C2C2C);
        g.RoundRectOutline(x + 0.5f, y + 0.5f, w - 1, h - 1, 8 * s, 0x26FFFFFF, 1);
        var items = MenuItems();
        for (int i = 0; i < items.Length; i++)
        {
            float iy = y + 4 * s + i * row;
            if (o.MenuItem == i) g.RoundRect(x + 4 * s, iy, w - 8 * s, row, 4 * s, 0x1AFFFFFF);
            g.Text(items[i], MenuFont(), x + 20 * s, iy, w - 40 * s, row, 0xFFFFFFFF);
        }
    }

    // The new name is kept (empty gives back "Desktop N") or dropped.
    void FinishRename(OverviewState o, bool keep)
    {
        if (o.Renaming is not Guid d) return;
        o.Renaming = null;
        _redraw = true;
        if (!keep) return;
        Desktops.Rename(d, o.Name.Trim());
        RefreshTitles();
        Log.Write("overview: desktop renamed");
    }

    // What a key types with the keyboard layout of the window in front.
    static string Typed(int vk, int scan)
    {
        var state = new byte[256];
        foreach (int k in new[] { 0x10, 0x11, 0x12, 0xA0, 0xA1, 0xA2, 0xA3, 0xA4, 0xA5 })
            if ((GetAsyncKeyState(k) & 0x8000) != 0) state[k] = 0x80;
        if ((GetKeyState(0x14) & 1) != 0) state[0x14] = 1;
        var layout = GetKeyboardLayout(GetWindowThreadProcessId(GetForegroundWindow(), out _));
        var buf = new System.Text.StringBuilder(8);
        // 4: leaves the keyboard's state alone, so a dead key in the window in front still works.
        int n = ToUnicodeEx((uint)vk, (uint)scan, state, buf, buf.Capacity, 4, layout);
        return n > 0 ? new string(buf.ToString()[..n].Where(c => !char.IsControl(c)).ToArray()) : "";
    }

    static bool Inside(Box b, int x, int y) => x >= b.X && x < b.Right && y >= b.Y && y < b.Bottom;

    // What a click landed on, for the log.
    string HitName(OverviewState o, int window, int x, int y) => window >= 0 ? "a window" : o.Strip.Hit(x, y) switch
    {
        { Kind: TileKind.Desktop } t => $"desktop {Desktops.IndexOf(t.Desktop) + 1}",
        { Kind: TileKind.NewLane } => "New lane",
        { Kind: TileKind.Trash } => "the bin",
        { } => "a +",
        null => "nothing",
    };

    // Closes a desktop; its windows go to the one next to it, as when Windows closes a desktop.
    void CloseDesktop(OverviewState o, Guid desktop)
    {
        if (Desktops.Count <= 1) return;
        Guid? fallback = _lanes.Neighbor(desktop, Direction.Left) ?? _lanes.Neighbor(desktop, Direction.Right)
            ?? _lanes.Neighbor(desktop, Direction.Up) ?? _lanes.Neighbor(desktop, Direction.Down);
        if (fallback is not Guid to) return;
        bool here = desktop == _current;
        // Windows moves to `to` and would show its pill.
        if (here) { WindowsPill.Hide(); _lastSwitchAt = Now; }
        Desktops.Remove(desktop, to);
        Log.Write($"closed a desktop; its windows went to desktop {Desktops.IndexOf(to) + 1}");
        if (here)
        {
            _current = Desktops.Current is var now && now != desktop ? now : to;
            _overlay.Place();
        }
        LanesChanged();
        Relayout(o);
        // The windows shown were that desktop's, or have just come to the one shown.
        var show = o.Page.Desktop == desktop ? _current : o.Page.Desktop;
        if (show != o.Page.Desktop || to == show)
        {
            var old = o.Page;
            o.Page = NewPage(show, o.Strip);
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
        // Typing a desktop's name: the keys go to the name, not to the window in front
        // (Shift, Ctrl, Alt and the Windows key still pass, for the layout and shortcuts).
        if (code >= 0 && _overview is { Renaming: not null } ro && Marshal.ReadInt32(lParam) is var vk
            && vk is not (0x10 or 0x11 or 0x12 or 0x14 or 0x5B or 0x5C) and not (>= 0xA0 and <= 0xA5))
        {
            if (wParam == (IntPtr)WM_KEYDOWN || wParam == (IntPtr)0x104 /* WM_SYSKEYDOWN */)
            {
                if (vk == 0x0D) FinishRename(ro, keep: true);
                else if (vk == 0x1B) FinishRename(ro, keep: false);
                else if (vk == 0x08) { if (ro.Name.Length > 0) ro.Name = ro.Name[..^1]; }
                else if (ro.Name.Length < 60) ro.Name += Typed(vk, Marshal.ReadInt32(lParam, 4));
                _redraw = true;
            }
            return 1;
        }
        if (code >= 0 && wParam == (IntPtr)WM_KEYDOWN && Marshal.ReadInt32(lParam) == 0x1B)
        {
            if (_overview is { MenuFor: not null } om)
            {
                om.MenuFor = null;
                _redraw = true;
            }
            else if (_overview is { Drag: { } d } o)
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
        var page = o.Page;
        g.Begin(Gfx.Color(0xFF000000));

        // The desktop itself under the windows, sharp and with its icons, a little darker. A
        // four-finger swipe slides it away with its windows, and the next desktop comes in beside
        // it, as a swipe outside the overview does.
        double ox = sw == null || vertical ? 0 : sw.Offset, oy = vertical ? sw!.Offset : 0;
        double step = sw == null ? 0 : sw.Track.Page * (f < 0 ? 1 : -1);
        float px = (float)ox + (vertical ? 0 : (float)step), py = (float)oy + (vertical ? (float)step : 0);
        if (DeskPicture(page.Desktop) is { } desk) g.Image(desk, (float)ox, (float)oy, g.Width, g.Height);
        if (toward is Guid tw && DeskPicture(tw) is { } theirs) g.Image(theirs, px, py, g.Width, g.Height);
        g.Rect(0, 0, g.Width, g.Height, A(0xFF000000, 0.25f * pc));

        // Where each window is at this moment.
        var boxes = new Box[page.Thumbs.Count];
        double highest = double.MaxValue;
        for (int i = 0; i < boxes.Length; i++)
        {
            boxes[i] = page.From[i].Lerp(page.Shown[i], Math.Clamp(p, -0.05, 1.08));
            highest = Math.Min(highest, boxes[i].Y);
        }

        // Lane rows on a frosted band, the only blurred part. The band slides down whole from
        // above the screen just ahead of the windows, so it never overlaps them (as the Spaces bar
        // does in Mission Control), and goes back up whole: it never fades, so what it covers
        // never shows through it. Under the band the desktops slide too, blurred.
        float sp = Math.Clamp(p / 0.6f, 0, 1), sq = 1 - MathF.Pow(1 - sp, 3);
        float a = keep, dy = (1 - sq) * -((float)o.Strip.Bottom + 2 * s);
        if (boxes.Length > 0) dy = Math.Min(dy, Math.Min(0, (float)highest - 30 * s - (float)o.Strip.Bottom));
        float band = (float)o.Strip.Bottom + dy;
        if (a > 0.002f && band > 0)
        {
            g.Clip(0, 0, g.Width, band);
            if (Wallpaper(page.Desktop) is var (_, blurred)) g.Image(blurred, (float)ox, (float)oy, g.Width, g.Height, a);
            if (toward is Guid tb && Wallpaper(tb) is var (_, theirsBlurred)) g.Image(theirsBlurred, px, py, g.Width, g.Height, a);
            g.Rect(0, 0, g.Width, band, A(0xFF000000, 0.3f * a));
            g.Unclip();
            g.Rect(0, band - 1, g.Width, 1, A(0x1FFFFFFF, a));
        }
        if (a > 0.002f) DrawStrip(o, a, dy, f, toward, p);

        // The windows of this desktop, and of the one a four-finger swipe brings in, each with
        // its shadow, a deeper one under the pointer. No names: the windows speak for themselves.
        float sa = keep * Math.Clamp(p, 0, 1);
        void Lift(Box b, bool high)
        {
            if (sa > 0.002f) g.Shadow((float)b.X, (float)b.Y, (float)b.W, (float)b.H, 8 * s, (high ? 46 : 28) * s, (high ? 16 : 10) * s, (high ? 0.9f : 0.6f) * sa);
        }
        // Going up or down, windows pass under the band.
        float clip = vertical ? band : float.NegativeInfinity;
        if (vertical) g.Clip(0, clip, g.Width, g.Height - clip);
        for (int i = 0; i < boxes.Length; i++)
        {
            if (o.Drag?.Window == i) continue;
            var b = boxes[i] with { X = boxes[i].X + ox, Y = boxes[i].Y + oy };
            Lift(b, o.Hover == i && o.Idle);
            page.Thumbs[i].Place((float)b.X, (float)b.Y, (float)b.W, (float)b.H, keep, clip);
        }
        foreach (var unused in new[] { sw?.PreviousPage, sw?.NextPage })
            if (unused != null && unused != other) unused.Hide();
        if (other != null)
        {
            for (int i = 0; i < other.Thumbs.Count; i++)
            {
                var b = other.To[i] with { X = other.To[i].X + ox + (vertical ? 0 : step), Y = other.To[i].Y + oy + (vertical ? step : 0) };
                Lift(b, false);
                other.Thumbs[i].Place((float)b.X, (float)b.Y, (float)b.W, (float)b.H, keep, clip);
            }
        }
        if (vertical) g.Unclip();

        if (o.Drag is { Window: >= 0 } dw)
        {
            var b = DraggedWindowBox(o, dw);
            page.Thumbs[dw.Window].Place((float)b.X, (float)b.Y, (float)b.W, (float)b.H);
        }

        if (o.MenuFor != null && o.Idle) DrawMenu(o);
        float bar = 1;
        if (o.Zoom is { } zz && FullScreen(zz.Target)) bar *= 1 - Math.Clamp(z, 0, 1);
        PlaceTaskbar(bar);

        if (o.Zoom is { } zoom && Picture(zoom.Target) is { } target)
        {
            var b = zoom.From.Lerp(new Box(0, 0, g.Width, g.Height), Math.Clamp(z, 0, 1));
            g.RoundImage(target, (float)b.X, (float)b.Y, (float)b.W, (float)b.H, 8 * s * keep, 1);
        }
        g.End();
    }

    void DrawStrip(OverviewState o, float a, float dy, float f, Guid? toward, float p)
    {
        var g = _gfx;
        float s = _overlay.Scale;
        var capFont = g.Font(14 * s, FontWeight.Normal, centred: true);
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
            float x = (float)r.X + (t.Lane == lane && t.Kind == TileKind.Desktop ? shift : 0), y = (float)r.Y + dy, w = (float)r.W, h = (float)r.H;
            bool targeted = drag?.Target == t || drag?.Drop is { NewLane: true } nd && t.Kind == TileKind.NewLane && t.Lane == nd.Lane;
            bool hover = o.HoverTile == t && o.Idle;
            if (t.Kind is TileKind.Plus or TileKind.Trash)
            {
                // A round button in the corner: "+", or the bin while a desktop is dragged. It
                // lights up under the pointer (red for the bin) and says what it does.
                bool bin = t.Kind == TileKind.Trash;
                uint accent = bin ? 0xFFE5484Du : 0xFF4CC2FFu;
                float rr = w / 2, cx = x + rr, cy = y + rr, arm = 7 * s;
                if (hover || targeted) g.RoundRect(x, y, w, h, rr, A(targeted ? (accent & 0xFFFFFF) | 0x33000000u : 0x1FFFFFFFu, a));
                g.RoundRectOutline(x + 0.5f, y + 0.5f, w - 1, h - 1, rr, targeted ? A(accent, a) : A(hover ? 0x59FFFFFFu : 0x33FFFFFFu, a), targeted ? 2 * s : 1);
                uint ink = A(hover || targeted ? 0xFFFFFFFFu : 0x8CFFFFFFu, a);
                if (bin)
                {
                    g.Rect(cx - 7 * s, cy - 5.5f * s, 14 * s, 1.5f * s, ink);
                    g.Rect(cx - 2.5f * s, cy - 7.5f * s, 5 * s, 1.5f * s, ink);
                    g.RoundRectOutline(cx - 5 * s, cy - 3 * s, 10 * s, 11 * s, 1.5f * s, ink, 1.5f * s);
                }
                else
                {
                    g.Rect(cx - arm, cy - 0.75f * s, 2 * arm, 1.5f * s, ink);
                    g.Rect(cx - 0.75f * s, cy - arm, 1.5f * s, 2 * arm, ink);
                }
                if (hover || targeted)
                {
                    string tip = bin ? "Close desktop" : "New desktop";
                    float tw = g.TextWidth(tip, capFont);
                    g.Text(tip, capFont, Math.Min(cx - tw / 2, g.Width - tw - 8 * s), y + h + 6 * s, tw, 18 * s, A(0xFFE6E6E6, a));
                }
                continue;
            }
            if (t.Kind == TileKind.NewLane)
            {
                // Where a new lane would go: a dashed place, blue when something is over it.
                if (targeted)
                {
                    g.RoundRect(x, y, w, h, 8 * s, A(0x264CC2FFu, a));
                    g.RoundRectOutline(x + 1, y + 1, w - 2, h - 2, 8 * s, A(0xFF4CC2FF, a), 2 * s);
                }
                else g.DashedRoundRectOutline(x + 0.75f, y + 0.75f, w - 1.5f, h - 1.5f, 8 * s, A(0x59FFFFFFu, a), 1.5f * s);
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
            // White around the desktop whose windows are shown, when it is not the one you are on.
            else if ((hover || t.Desktop == o.Page.Desktop) && t.Desktop != _current) g.RoundRectOutline(x - 4 * s, y - 4 * s, w + 8 * s, h + 8 * s, 11 * s, A(0xD9FFFFFF, a), 2 * s);
            if (o.Added == t.Desktop && 1 - (float)((Now - o.AddedAt) / 0.6) is var lit and > 0)
                g.RoundRectOutline(x - 4 * s, y - 4 * s, w + 8 * s, h + 8 * s, 11 * s, A(0xFFFFFFFF, a * lit), 2 * s);
            if (o.Renaming == t.Desktop)
            {
                // The name being typed, in a field where the caption was.
                var font = g.Font(14 * s);
                float fx = x - 6 * s, fy = y + h + 3 * s, fw = w + 12 * s, fh = 22 * s;
                g.RoundRect(fx, fy, fw, fh, 5 * s, A(0xFF1F1F1F, ta));
                g.RoundRectOutline(fx + 0.75f, fy + 0.75f, fw - 1.5f, fh - 1.5f, 5 * s, A(0xFF4CC2FF, ta), 1.5f * s);
                float nw = Math.Min(g.TextWidth(o.Name, font), fw - 18 * s);
                g.Text(o.Name, font, fx + 8 * s, fy, nw, fh, A(0xFFFFFFFF, ta));
                g.Rect(fx + 8 * s + nw + 1, fy + 4 * s, 1.5f * s, fh - 8 * s, A(0xFFFFFFFF, ta));
                continue;
            }
            string title = Title(t.Desktop);
            float cw = Math.Min(g.TextWidth(title, capFont), w);
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

    delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] static extern IntPtr SetWindowsHookEx(int id, HookProc proc, IntPtr module, uint thread);
    [DllImport("user32.dll")] static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")] static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] static extern short GetAsyncKeyState(int vk);
    [DllImport("user32.dll")] static extern short GetKeyState(int vk);
    [DllImport("user32.dll")] static extern IntPtr GetKeyboardLayout(uint thread);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int ToUnicodeEx(uint vk, uint scan, byte[] state, System.Text.StringBuilder buf, int size, uint flags, IntPtr layout);
}
