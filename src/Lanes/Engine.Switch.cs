using Lanes.Core;
using Vortice.Direct2D1;
using Vortice.DirectWrite;
using static Lanes.Native;

namespace Lanes;

sealed partial class Engine
{
    sealed class SwitchState
    {
        public Axis Axis;
        public SwipeTrack Track = null!;
        public Guid From;
        public Guid? Previous, Next;
        public ID2D1Bitmap1 FromPicture = null!;
        public ID2D1Bitmap1? PreviousPicture, NextPicture;
        public double Offset;
        public Spring? Spring;
        public double SpringStart;
        public Outcome Outcome;
        public double Start;
        // Windows has switched to where the picture is landing.
        public bool Switched;
        public double SwitchedAt;
        // Live pictures of the windows of the desktops beside you, over their desktops' own
        // pictures (and of the one you set out from, when a swipe starts while the last lands).
        public List<(AppWindow Window, Thumbnail Thumb)> PreviousWindows = new(), NextWindows = new(), FromWindows = new();
    }

    // When Windows last switched desktops; its "Desktop N" pill stays about two seconds.
    double _lastSwitchAt = -10;

    // Every switch Lanes makes: Windows' pill is kept away first (see WindowsPill). Windows takes
    // a tenth of a second or more to switch and hand the focus over, which would hold up a frame
    // of the swipe still moving, so switches run in order on a thread of their own; `_switching`
    // completes when the last has, and Lanes' picture stays until then.
    void SwitchTo(Guid desktop)
    {
        WindowsPill.Hide();
        _lastSwitchAt = Now;
        var done = new TaskCompletionSource();
        _switching = done.Task;
        Switches.Add(() =>
        {
            try { if (!Desktops.Switch(desktop)) Log.Write("switch failed"); }
            catch (Exception e) { Log.Write($"switch: {e.Message}"); }
            done.SetResult();
            // Over whatever Windows brought forward.
            Post(() => { if (_overlay.Shown) _overlay.Place(); });
        });
    }

    Task _switching = Task.CompletedTask;
    static readonly System.Collections.Concurrent.BlockingCollection<Action> Switches = new();

    static Engine()
    {
        var t = new Thread(() =>
        {
            // A message queue, which handing the focus over needs (AttachThreadInput).
            MessageLoop.PeekMessage(out _, IntPtr.Zero, 0, 0, 0);
            foreach (var job in Switches.GetConsumingEnumerable()) job();
        }) { IsBackground = true, Name = "desktop switches" };
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
    }

    void BeginSwitch(Axis axis)
    {
        LookAtTheme();
        ID2D1Bitmap1? from = null;
        List<(AppWindow, Thumbnail)>? fromWindows = null;
        // Lanes' pill is already up from the last swipe: it carries on from where it is.
        bool pillUp = _switch != null || _map.Shown;
        if (_switch is { } pending)
        {
            // A new swipe while the last one is still settling: finish that one now, under the
            // picture, and start from where it was going, with its windows' live pictures.
            from = pending.Outcome switch { Outcome.Next => pending.NextPicture, Outcome.Previous => pending.PreviousPicture, _ => pending.FromPicture };
            fromWindows = pending.Outcome switch { Outcome.Next => pending.NextWindows, Outcome.Previous => pending.PreviousWindows, _ => pending.FromWindows };
            if (pending.Outcome == Outcome.Next) pending.NextWindows = new();
            else if (pending.Outcome == Outcome.Previous) pending.PreviousWindows = new();
            else pending.FromWindows = new();
            FinishSwitch(keepOverlay: true);
            DiscardTouchCapture();
        }
        else
        {
            _current = Desktops.Current;
            Remember(_current, TakeTouchCapture());
        }
        RefreshTitles();
        var s = new SwitchState
        {
            Axis = axis,
            Start = pillUp ? Now - 1 : Now,
            From = _current,
            Previous = _lanes.Neighbor(_current, axis == Axis.Horizontal ? Direction.Left : Direction.Up),
            Next = _lanes.Neighbor(_current, axis == Axis.Horizontal ? Direction.Right : Direction.Down),
        };
        if (!_overlay.Shown) UseArea();
        // A whole step is a screen: desktops lie edge to edge, as if one long screen.
        double page = axis == Axis.Horizontal ? _gfx.Width : _gfx.Height;
        s.Track = new SwipeTrack(page, s.Previous != null, s.Next != null, _tuning);
        s.FromPicture = from ?? Picture(_current)!;
        s.FromWindows = fromWindows ?? new();
        s.PreviousPicture = s.Previous is Guid p ? Beside(p, s.PreviousWindows) : null;
        s.NextPicture = s.Next is Guid n ? Beside(n, s.NextWindows) : null;
        _switch = s;
        if (!pillUp) _pillX = _pillAside = null;
        _gfx.Frames.Clear();
        // Over the windows' live pictures, which were registered just now.
        RaiseTaskbar();
        if (!_overlay.Shown)
        {
            _overlay.Place();
            RenderSwitch();
            _overlay.Show();
        }
        Log.Write($"switch {axis} from {Title(_current)}; previous {(s.Previous is Guid a ? Title(a) : "-")}, next {(s.Next is Guid b ? Title(b) : "-")}; {CaptureNote}");
    }

    // A desktop beside you: live pictures of its windows, which keep drawing there (see Awake),
    // over the picture Lanes has of that desktop, with the same windows in the same places, its
    // icons and the windows' shadows (should a live picture miss a frame, the window is still
    // there). Lanes knows the windows from its look at the other desktops each second.
    ID2D1Bitmap1? Beside(Guid desktop, List<(AppWindow, Thumbnail)> live)
    {
        if (_windowsSeen.TryGetValue(desktop, out var seen))
            foreach (var w in seen) live.Add((w, new Thumbnail(_overlay.Handle, w.Handle)));
        return Picture(desktop);
    }

    double Along(double dx, double dy, Axis axis)
    {
        if (axis == Axis.Horizontal) return dx;
        return _settings.Natural ? dy : -dy;
    }

    void MoveSwitch(double dx, double dy)
    {
        var s = _switch!;
        if (s.Spring != null) return;
        s.Offset = s.Track.OffsetFor(Along(dx, dy, s.Axis));
    }

    void EndSwitch(double vx, double vy)
    {
        var s = _switch!;
        double speed = s.Track.SpeedFor(Along(vx, vy, s.Axis));
        var (outcome, target) = s.Track.Release(s.Offset, speed);
        s.Outcome = outcome;
        s.Spring = new Spring(s.Offset, speed, target, _tuning.SwitchResponse);
        s.SpringStart = Now;
        Log.Write($"release at {s.Offset / s.Track.Page:0.00} screen, {speed / s.Track.Page:0.00} screens/s: {outcome}{Reported()}");
    }

    void StepSwitch(double now)
    {
        var s = _switch!;
        if (s.Spring == null) return;
        double t = now - s.SpringStart;
        s.Offset = s.Spring.At(t);
        bool done = s.Spring.Done(t);
        if (done) s.Offset = s.Spring.Target;
        // Windows switches as the picture comes to rest, a few pixels short of it (switching while
        // the desktops still move would make Windows' own work show in the motion): its pill then
        // comes up where Lanes' copy has arrived, and Windows has a moment to draw the new desktop
        // before the picture goes.
        if (!s.Switched && Math.Abs(s.Offset - s.Spring.Target) < 0.004 * s.Track.Page) SwitchNow(s);
        if (!done) return;
        if (s.Switched && (now - s.SwitchedAt < 0.2 || !_switching.IsCompleted)) return;
        RenderSwitch();
        FinishSwitch();
    }

    // Moves Windows to the desktop the swipe is landing on, once.
    void SwitchNow(SwitchState s)
    {
        if (s.Switched || (s.Outcome == Outcome.Next ? s.Next : s.Outcome == Outcome.Previous ? s.Previous : null) is not Guid to) return;
        SwitchTo(to);
        _overlay.Place();
        s.Switched = true;
        s.SwitchedAt = _lastSwitchAt;
        _current = to;
        _lanes.Visited(to);
        _lanesChanged = true;
    }

    // The lanes remember where you were in each; saved once the picture has gone.
    bool _lanesChanged;

    void FinishSwitch(bool keepOverlay = false)
    {
        var s = _switch!;
        SwitchNow(s);
        _switch = null;
        foreach (var (_, thumb) in s.PreviousWindows.Concat(s.NextWindows).Concat(s.FromWindows)) thumb.Dispose();
        if (keepOverlay) return;
        LingerPill(s);
        _overlay.Hide();
        DropTaskbar();
        if (_lanesChanged) Settings.SaveLanes(_lanes);
        _lanesChanged = false;
        Log.Write($"switch drawn: {_gfx.FrameStats()}{(s.Switched ? $"; Windows switched {(Now - s.SwitchedAt) * 1000:0} ms before the picture went" : "")}");
    }

    void RenderSwitch()
    {
        var s = _switch!;
        var g = _gfx;
        float w = g.Width, h = g.Height, o = (float)s.Offset;
        bool horizontal = s.Axis == Axis.Horizontal;
        float page = horizontal ? w : h; // one screen; desktops lie edge to edge
        g.Begin(Gfx.Color(_light ? 0xFFD4D4D4 : 0xFF262626));
        Draw(s.FromPicture, o);
        ID2D1Bitmap1? other = o < 0 ? s.NextPicture : o > 0 ? s.PreviousPicture : null;
        float otherAt = o < 0 ? o + page : o - page;
        if (other != null) Draw(other, otherAt);
        // Past the end of a lane the desktop gives a little, lifted off a plain backdrop: its edge
        // casts a short shadow there.
        else if (Math.Abs(o) >= 0.5f) EdgeShadow(o > 0 ? o : o + page, o > 0 ? -1 : 1, Math.Abs(o));
        Live(s.FromWindows, o);
        Live(s.PreviousWindows, o > 0 ? otherAt : null);
        Live(s.NextWindows, o < 0 ? otherAt : null);
        // One taskbar, the live one, stays put over all desktops; their pictures have an empty bar
        // in its place. A full-screen window lies over the taskbar, as on the screen: the taskbar
        // comes out from under it, at full strength, where it moves off, and goes under one that
        // comes in.
        var covers = new List<(float, float)>();
        if (FullScreen(s.From)) covers.Add((o, o + page));
        Guid? otherDesktop = o < 0 ? s.Next : o > 0 ? s.Previous : null;
        if (other != null && FullScreen(otherDesktop)) covers.Add((otherAt, otherAt + page));
        // The shadow comes as the desktops move, not at once at the first touch, and goes as they
        // come to rest, so there is none on the desktop the swipe lands on.
        PlaceTaskbar(horizontal, covers, Math.Clamp(Math.Min(Math.Abs(o), page - Math.Abs(o)) / (0.1f * page), 0, 1));
        g.End();
        // Lanes' pill stays put over both desktops, in its own window above the live pictures.
        float hud = HudAlpha(s);
        ShowPill(p => DrawPill(s, p, hud));

        void Draw(ID2D1Bitmap1 bmp, float at)
        {
            if (horizontal) g.Image(bmp, at, 0, w, h);
            else g.Image(bmp, 0, at, w, h);
        }

        // From a desktop's edge at `at` over the backdrop beside it (`way` -1 before the edge,
        // 1 after it, `room` wide): darkest at the edge, gone within EdgeReach.
        void EdgeShadow(float at, int way, float room)
        {
            float reach = EdgeReach * _overlay.Scale;
            for (int i = 0; i < reach && i < room; i++)
            {
                uint argb = A(0xFF000000, 0.5f * MathF.Pow(1 - (i + 0.5f) / reach, 2));
                float line = way < 0 ? at - i - 1 : at + i;
                if (horizontal) g.Rect(line, 0, 1, h, argb);
                else g.Rect(0, line, w, 1, argb);
            }
        }

        // Each window where it is on its desktop, cut at the desktop's edges.
        void Live(List<(AppWindow Window, Thumbnail Thumb)> windows, float? at)
        {
            foreach (var (win, thumb) in windows)
            {
                if (at is not float a) { thumb.Place(0, 0, 1, 1, 0); continue; }
                var f = win.Frame;
                float x = f.Left - _overlay.Area.Left + (horizontal ? a : 0), y = f.Top - _overlay.Area.Top + (horizontal ? 0 : a);
                thumb.PlaceCut(x, y, f.Width, f.Height, a, a + page, horizontal);
            }
        }
    }

    const float EdgeReach = 24;

    // Lanes' pill comes in quickly as a swipe starts, and stays after it (see LingerPill).
    float HudAlpha(SwitchState s) => (float)Math.Clamp((Now - s.Start) / 0.12, 0, 1);

    // The bottom of the screen above the taskbar, in the overlay's own coordinates.
    float WorkBottom => _overlay.Work.Bottom - _overlay.Area.Top;

    // Lanes' pill, in the place of Windows' "Desktop N" one: the overview's lanes in miniature.
    // As there, each lane lies with the desktop it would take you to in the middle column, where
    // the blue cell is, and your lane is lighter. A sideways swipe slides your lane under the
    // blue cell; one up or down carries the blue cell, and the lighter row, straight to the next
    // lane, and your lane's light goes with it: a light row slides from one lane's cells to the
    // other's, seen only inside the cells. The pill is as wide as the longest lane could need
    // either way, so it keeps its size and place. Windows' volume or brightness pop-up shows in
    // the same place: the pill glides aside while it shows and back once it has gone. Should
    // Windows' pill come up anyway (Lanes could not find its window), Lanes' one goes above it.
    sealed record Pill(float X, float Y, float W, float H);

    const float Cell = 14, CellH = 9, CellGap = 3, PillPad = 12;

    // Columns in the pill: the longest lane can lie fully to either side of the middle one.
    int PillColumns => 2 * Math.Max(1, _lanes.Lanes.Max(l => l.Desktops.Count)) - 1;

    Pill PillFor()
    {
        float s = _overlay.Scale;
        int n = _lanes.Lanes.Count, cols = PillColumns;
        float w = MathF.Round((cols * Cell + (cols - 1) * CellGap + 2 * PillPad) * s);
        float h = MathF.Round((n * CellH + (n - 1) * CellGap + 2 * PillPad) * s);
        float bottom = WorkBottom - (WindowsPill.Available ? 13 : 77) * s;
        float home = MathF.Round((_gfx.Width - w) / 2), x = home;
        // Beside Windows' pop-up while it shows.
        var osd = WindowsPill.ShownAt();
        _pillAside = osd is { } o && o.Left - _overlay.Area.Left < home + w && o.Right - _overlay.Area.Left > home ? o.Left - _overlay.Area.Left - 12 * s - w : null;
        if (_pillAside is float aside) x = aside;
        _pillX = _pillX is float was && Math.Abs(was - x) > 0.5f ? was + (x - was) * 0.2f : x;
        return new Pill(MathF.Round(_pillX.Value), MathF.Round(bottom - h), w, h);
    }

    float? _pillX, _pillAside;

    // Where the cell in `column` of lane `l` is drawn in the pill.
    (float X, float Y) CellAt(Pill p, int l, float column)
    {
        float s = _overlay.Scale;
        return (p.X + (PillPad + column * (Cell + CellGap)) * s, p.Y + (PillPad + l * (CellH + CellGap)) * s);
    }

    // The pill during a swipe: `k` (0..1) is how far it has gone toward the desktop it would land on.
    void DrawPill(SwitchState sw, Pill p, float alpha)
    {
        float f = (float)Math.Clamp(sw.Offset / sw.Track.Page, -1, 1);
        DrawPill(p, sw.From, f < 0 ? sw.Next : f > 0 ? sw.Previous : null, Math.Abs(f), -f, alpha);
    }

    // `stretch` moves your lane a little when there is no desktop to go to (the picture's give
    // at a lane's end).
    void DrawPill(Pill p, Guid from, Guid? toward, float k, float stretch, float alpha)
    {
        var g = _gfx;
        float s = _overlay.Scale, cell = Cell * s, cellH = CellH * s;
        // In Windows' light or dark mode, as its own pop-ups are: on dark, light cells and your
        // lane lighter; on light, dark cells and your lane darker.
        var (edgeInk, fill, cellInk, laneInk, here) = _light
            ? (0x1A000000u, 0xF5F9F9F9u, 0x1F000000u, 0x2E000000u, 0xFF005FB8u)
            : (0x332C2C2Cu, 0xF52C2C2Cu, 0x24FFFFFFu, 0x3EFFFFFFu, 0xFF4CC2FFu);
        g.RoundRect(p.X, p.Y, p.W, p.H, 8 * s, A(edgeInk, alpha));
        g.RoundRect(p.X + 1, p.Y + 1, p.W - 2, p.H - 2, 7 * s, A(fill, alpha));
        if (_lanes.Locate(from) is not var (fromLane, fromIndex)) return;
        var (toLane, toIndex) = toward is Guid t && _lanes.Locate(t) is var (tl, ti) ? (tl, ti) : (fromLane, fromIndex);
        float middle = (PillColumns - 1) / 2f;
        // Your lane's shift, in columns: toward the next desktop along it, or the give at its end.
        float slide = toward == null ? -stretch : toLane == fromLane ? -(toIndex - fromIndex) * k : 0;
        // The light row's top, between your lane's row and the next one's.
        float lightY = CellAt(p, fromLane, 0).Y + (CellAt(p, toLane, 0).Y - CellAt(p, fromLane, 0).Y) * k;
        for (int l = 0; l < _lanes.Lanes.Count; l++)
        {
            var desks = _lanes.Lanes[l].Desktops;
            int anchor = l == fromLane ? fromIndex : Math.Max(0, desks.IndexOf(_lanes.CurrentOf(l) ?? Guid.Empty));
            for (int i = 0; i < desks.Count; i++)
            {
                var (x, y) = CellAt(p, l, middle + i - anchor + (l == fromLane ? slide : 0));
                g.RoundRect(x, y, cell, cellH, 2.5f * s, A(cellInk, alpha));
                // Your lane's cells: more of the same ink over them (on dark, white at 0x3E over
                // the cell's 0x24 makes 0x59).
                float top = Math.Max(y, lightY), bottom = Math.Min(y + cellH, lightY + cellH);
                if (bottom - top < 0.05f) continue;
                g.Clip(x, top, cell, bottom - top);
                g.RoundRect(x, y, cell, cellH, 2.5f * s, A(laneInk, alpha));
                g.Unclip();
            }
        }
        var a = CellAt(p, fromLane, middle);
        var b = CellAt(p, toLane, middle);
        g.RoundRect(a.X, a.Y + (b.Y - a.Y) * k, cell, cellH, 2.5f * s, A(here, alpha));
    }

    // Whether Windows is in light mode for its own parts (Settings > Personalization > Colors),
    // which Lanes' pill and the backdrop of a swipe follow: looked at as a swipe or the overview
    // starts, and each second in between.
    bool _light;

    void LookAtTheme()
    {
        try { _light = Microsoft.Win32.Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "SystemUsesLightTheme", 0) is int v && v != 0; }
        catch { _light = false; }
    }

    // After a swipe (one that went back too) or a desktop picked in the overview, the pill stays
    // over the desktop as long as Windows' own would (about two seconds), in a small window of
    // its own, then fades.
    const double PillStays = 2.0, PillFades = 0.25;
    double _pillSince;
    Action<Pill>? _pillDraw;

    // The pill in its small window of its own, above the overlay and the live window pictures
    // DWM draws over everything Lanes draws there.
    void ShowPill(Action<Pill> draw)
    {
        var p = PillFor();
        using var pixels = _gfx.Offscreen((int)p.X, (int)p.Y, (int)p.W, (int)p.H, () => draw(p));
        _map.Show(pixels, _overlay.Area.Left + (int)p.X, _overlay.Area.Top + (int)p.Y);
    }

    void LingerPill(Action<Pill> draw, double since)
    {
        ShowPill(draw);
        // Shown before the overlay goes, so no frame has neither.
        DwmFlush();
        _pillDraw = draw;
        _pillSince = since;
    }

    void LingerPill(SwitchState s) => LingerPill(p => DrawPill(s, p, HudAlpha(s)), s.Switched ? s.SwitchedAt : Now);

    void StepLingeringPill(double now)
    {
        double t = now - _pillSince;
        if (t >= PillStays + PillFades)
        {
            _map.Hide();
            _pillX = _pillAside = null;
            return;
        }
        if (t > PillStays) _map.Fade((float)(1 - (t - PillStays) / PillFades));
        float before = _pillX ?? 0;
        var p = PillFor();
        if (Math.Abs(p.X - before) >= 0.5f) _map.Move(_overlay.Area.Left + (int)p.X, _overlay.Area.Top + (int)p.Y);
    }

    static uint A(uint argb, float alpha) => ((uint)Math.Clamp((argb >> 24) * alpha, 0, 255) << 24) | (argb & 0xFFFFFF);
}
