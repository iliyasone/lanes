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
    }

    // When Windows last switched desktops; its "Desktop N" pill stays about two seconds.
    double _lastSwitchAt = -10;

    // Every switch Lanes makes: Windows' pill is kept away first (see WindowsPill).
    void SwitchTo(Guid desktop)
    {
        WindowsPill.Hide();
        if (!Desktops.Switch(desktop)) Log.Write("switch failed");
        _lastSwitchAt = Now;
    }

    void BeginSwitch(Axis axis)
    {
        ID2D1Bitmap1? from = null;
        // Lanes' pill is already up from the last swipe: it carries on from where it is.
        bool pillUp = _switch != null || _map.Shown;
        if (_switch is { } pending)
        {
            // A new swipe while the last one is still settling: finish that one now, under the
            // picture, and start from where it was going.
            from = pending.Outcome switch { Outcome.Next => pending.NextPicture, Outcome.Previous => pending.PreviousPicture, _ => pending.FromPicture };
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
        // A whole step is a screen plus the gap between screens, so a finished swipe ends exactly in place.
        double page = (axis == Axis.Horizontal ? _gfx.Width : _gfx.Height) + GapDip * _overlay.Scale;
        s.Track = new SwipeTrack(page, s.Previous != null, s.Next != null, _tuning);
        s.FromPicture = from ?? Picture(_current)!;
        s.PreviousPicture = s.Previous is Guid p ? Picture(p) : null;
        s.NextPicture = s.Next is Guid n ? Picture(n) : null;
        _switch = s;
        if (!pillUp) _pillX = _pillAside = null;
        _gfx.Frames.Clear();
        if (!_overlay.Shown)
        {
            _overlay.Place();
            RaiseTaskbar();
            RenderSwitch();
            _overlay.Show();
            _map.Hide();
        }
        Log.Write($"switch {axis} from {Title(_current)}; previous {(s.Previous is Guid a ? Title(a) : "-")}, next {(s.Next is Guid b ? Title(b) : "-")}; {CaptureNote}");
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
        // Windows switches as the picture comes to rest, a few pixels short of it: its own pill
        // then comes up where Lanes' copy has arrived, and Windows has a moment to draw the new
        // desktop (its wallpaper takes a tenth of a second or more) before the picture goes.
        if (!s.Switched && Math.Abs(s.Offset - s.Spring.Target) < 0.004 * s.Track.Page) SwitchNow(s);
        if (!done) return;
        if (s.Switched && now - s.SwitchedAt < 0.2) return;
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
        float scale = _overlay.Scale, gap = GapDip * scale;
        float w = g.Width, h = g.Height, o = (float)s.Offset;
        bool horizontal = s.Axis == Axis.Horizontal;
        float page = horizontal ? w : h; // one screen; the step between screens is page + gap
        g.Begin(Gfx.Color(0xFF0A0C10));
        Draw(s.FromPicture, o);
        ID2D1Bitmap1? other = o < 0 ? s.NextPicture : o > 0 ? s.PreviousPicture : null;
        float otherAt = o < 0 ? o + page + gap : o - page - gap;
        if (other != null) Draw(other, otherAt);

        // Side by side, each desktop darkens toward the edge that faces the gap, so the two read
        // as separate sheets; the darkening goes as the gap reaches the screen's edge, so nothing
        // is left of it once a desktop is in place. Desktops one above the other go without it.
        float sh = 24 * scale, dark = 0.35f * Math.Clamp(Math.Min(Math.Abs(o), page + gap - Math.Abs(o)) / (80 * scale), 0, 1);
        if (horizontal && dark > 0.002f)
        {
            if (o < 0) { EdgeShadow(o + page - sh, false); if (other != null) EdgeShadow(otherAt, true); }
            if (o > 0) { EdgeShadow(o, true); if (other != null) EdgeShadow(otherAt + page - sh, false); }
        }

        // Lanes' pill stays put over both desktops.
        float hud = HudAlpha(s);
        if (hud > 0.002f) DrawPill(s, PillFor(), hud);
        // One taskbar, the live one, stays put over all desktops; their pictures have an empty bar
        // in its place. A desktop with a full-screen window has none: the taskbar comes in over
        // the first fifth of the way from it, and goes over the last fifth of the way to it.
        float bar = 1, fade = 0.2f * (page + gap);
        if (FullScreen(s.From)) bar = Math.Min(bar, Math.Clamp(Math.Abs(o) / fade, 0, 1));
        Guid? otherDesktop = o < 0 ? s.Next : o > 0 ? s.Previous : null;
        if (other != null && FullScreen(otherDesktop)) bar = Math.Min(bar, Math.Clamp((page + gap - Math.Abs(o)) / fade, 0, 1));
        PlaceTaskbar(bar);
        g.End();

        void Draw(ID2D1Bitmap1 bmp, float at)
        {
            if (horizontal) g.Image(bmp, at, 0, w, h);
            else g.Image(bmp, 0, at, w, h);
        }

        // A strip `sh` wide starting at `at`; dark at its start when `darkFirst`, else at its end.
        void EdgeShadow(float at, bool darkFirst) => g.Fade(at, 0, sh, h, true, darkFirst, dark);
    }

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
    // either way, so it keeps its size
    // and place. Windows' volume
    // or brightness pop-up shows in the same place: the pill then stands beside it. Should Windows'
    // pill come up anyway (Lanes could not find its window), Lanes' one goes above it.
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
        // Beside Windows' pop-up, from when it shows until this pill goes, so the two do not shift
        // against each other.
        var osd = WindowsPill.ShownAt();
        if (osd is { } o && o.Left - _overlay.Area.Left < home + w && o.Right - _overlay.Area.Left > home) _pillAside = o.Left - _overlay.Area.Left - 12 * s - w;
        if (_pillAside is float aside) x = aside;
        _pillX = _pillX is float was && Math.Abs(was - x) > 0.5f ? was + (x - was) * 0.35f : x;
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
        g.RoundRect(p.X, p.Y, p.W, p.H, 8 * s, A(0x332C2C2C, alpha));
        g.RoundRect(p.X + 1, p.Y + 1, p.W - 2, p.H - 2, 7 * s, A(0xF52C2C2C, alpha));
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
                g.RoundRect(x, y, cell, cellH, 2.5f * s, A(0x24FFFFFF, alpha));
                // White at 0x3E over the cell's 0x24 makes the light cells' 0x59.
                float top = Math.Max(y, lightY), bottom = Math.Min(y + cellH, lightY + cellH);
                if (bottom - top < 0.05f) continue;
                g.Clip(x, top, cell, bottom - top);
                g.RoundRect(x, y, cell, cellH, 2.5f * s, A(0x3EFFFFFF, alpha));
                g.Unclip();
            }
        }
        var a = CellAt(p, fromLane, middle);
        var b = CellAt(p, toLane, middle);
        g.RoundRect(a.X, a.Y + (b.Y - a.Y) * k, cell, cellH, 2.5f * s, A(0xFF4CC2FF, alpha));
    }

    // After a swipe (one that went back too) or a desktop picked in the overview, the pill stays
    // over the desktop as long as Windows' own would (about two seconds), in a small window of
    // its own, then fades.
    const double PillStays = 2.0, PillFades = 0.25;
    double _pillSince;
    Action<Pill>? _pillDraw;

    void LingerPill(Action<Pill> draw, double since)
    {
        var p = PillFor();
        using var pixels = _gfx.Offscreen((int)p.X, (int)p.Y, (int)p.W, (int)p.H, () => draw(p));
        _map.Show(pixels, _overlay.Area.Left + (int)p.X, _overlay.Area.Top + (int)p.Y);
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
