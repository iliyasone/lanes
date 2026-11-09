using Lanes.Core;
using Vortice.Direct2D1;
using Vortice.DirectWrite;
using static Lanes.Native;

namespace Lanes;

sealed partial class Engine
{
    sealed class SwitchState
    {
        // The live taskbar is shown over the desktops (none of them has a full-screen window).
        public bool TaskbarStays;
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

    void BeginSwitch(Axis axis)
    {
        ID2D1Bitmap1? from = null;
        // Lanes' pill is already up from the last swipe: it carries on from where it is.
        bool pillUp = _switch != null || _map.Shown;
        WindowsPill.Hide();
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
        // With a full-screen window on any of the desktops, each desktop's picture brings its own
        // taskbar, or none; otherwise the live taskbar stays over all of them.
        s.TaskbarStays = !(FullScreen(s.From) || FullScreen(s.Previous) || FullScreen(s.Next));
        if (!_overlay.Shown) UseArea();
        // A whole step is a screen plus the gap between screens, so a finished swipe ends exactly in place.
        double page = (axis == Axis.Horizontal ? _gfx.Width : _gfx.Height) + GapDip * _overlay.Scale;
        s.Track = new SwipeTrack(page, s.Previous != null, s.Next != null, _tuning);
        s.FromPicture = from ?? Picture(_current)!;
        s.PreviousPicture = s.Previous is Guid p ? Picture(p) : null;
        s.NextPicture = s.Next is Guid n ? Picture(n) : null;
        _switch = s;
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
        if (!Desktops.Switch(to)) Log.Write("switch failed");
        _overlay.Place();
        s.Switched = true;
        s.SwitchedAt = _lastSwitchAt = Now;
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
        PlaceTaskbar(s.TaskbarStays ? 1 : 0);
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

    // A copy of the pill Windows shows after a switch, in the same place and the same look; the
    // overview shows it as it zooms into a desktop, and Windows' own one comes up over it.
    void DesktopPill(Guid desktop, float at, float alpha)
    {
        if (alpha <= 0.002f) return;
        var g = _gfx;
        float s = _overlay.Scale;
        var font = g.Font(14 * s, FontWeight.Normal, centred: true, family: "Segoe UI Variable Text");
        string name = WindowsName(desktop);
        float w = MathF.Round(g.TextWidth(name, font) + 69 * s), h = MathF.Round(48 * s);
        float x = MathF.Round((g.Width - w) / 2), y = MathF.Round(WorkBottom - 13 * s - h);
        if (_switch?.Axis == Axis.Vertical) y += MathF.Round(at); else x += MathF.Round(at);
        g.RoundRect(x, y, w, h, 8 * s, A(0x332C2C2C, alpha));
        g.RoundRect(x + 1, y + 1, w - 2, h - 2, 7 * s, A(0xFF2C2C2C, alpha));
        g.Text(name, font, x, y, w, h, A(0xFFFFFFFF, alpha));
    }

    // The bottom of the screen above the taskbar, in the overlay's own coordinates.
    float WorkBottom => _overlay.Work.Bottom - _overlay.Area.Top;

    // Lanes' pill, in the place of Windows' "Desktop N" one: the lanes in miniature, each lane a
    // row of its desktops, centred under each other. A blue cell is where you are and your lane
    // is lighter. The pill keeps its size and place; the blue cell follows the fingers to the
    // desktop the swipe goes to, and the lighter row goes with it to another lane. Should Windows'
    // pill still come up (Lanes could not find its window), Lanes' one goes above it.
    sealed record Pill(float X, float Y, float W, float H);

    const float Cell = 14, CellH = 9, CellGap = 3, PillPad = 12;

    Pill PillFor()
    {
        float s = _overlay.Scale;
        int n = _lanes.Lanes.Count, cols = Math.Max(1, _lanes.Lanes.Max(l => l.Desktops.Count));
        float w = MathF.Round((cols * Cell + (cols - 1) * CellGap + 2 * PillPad) * s);
        float h = MathF.Round((n * CellH + (n - 1) * CellGap + 2 * PillPad) * s);
        float bottom = WorkBottom - (WindowsPill.Hidden ? 13 : 77) * s;
        return new Pill(MathF.Round((_gfx.Width - w) / 2), MathF.Round(bottom - h), w, h);
    }

    // Where desktop `i` of lane `l` is drawn in the pill.
    (float X, float Y) CellAt(Pill p, int l, float i)
    {
        float s = _overlay.Scale;
        int cols = Math.Max(1, _lanes.Lanes.Max(x => x.Desktops.Count));
        float indent = (cols - _lanes.Lanes[l].Desktops.Count) / 2f;
        return (p.X + (PillPad + (indent + i) * (Cell + CellGap)) * s, p.Y + (PillPad + l * (CellH + CellGap)) * s);
    }

    void DrawPill(SwitchState sw, Pill p, float alpha)
    {
        var g = _gfx;
        float s = _overlay.Scale, cell = Cell * s, cellH = CellH * s;
        g.RoundRect(p.X, p.Y, p.W, p.H, 8 * s, A(0x332C2C2C, alpha));
        g.RoundRect(p.X + 1, p.Y + 1, p.W - 2, p.H - 2, 7 * s, A(0xF52C2C2C, alpha));
        if (_lanes.Locate(sw.From) is not var (fromLane, fromIndex)) return;
        // How far the swipe has gone toward the desktop it would land on, 0..1.
        float f = (float)Math.Clamp(sw.Offset / sw.Track.Page, -1, 1);
        Guid? toward = f < 0 ? sw.Next : f > 0 ? sw.Previous : null;
        var (toLane, toIndex) = toward is Guid t && _lanes.Locate(t) is var (tl, ti) ? (tl, ti) : (fromLane, fromIndex);
        float k = Math.Abs(f);
        for (int l = 0; l < _lanes.Lanes.Count; l++)
        {
            float light = toLane == fromLane ? (l == fromLane ? 1 : 0) : l == fromLane ? 1 - k : l == toLane ? k : 0;
            uint colour = A(0xFFFFFFFF, (0x24 + (0x59 - 0x24) * light) / 255f);
            for (int i = 0; i < _lanes.Lanes[l].Desktops.Count; i++)
            {
                var (x, y) = CellAt(p, l, i);
                g.RoundRect(x, y, cell, cellH, 2.5f * s, A(colour, alpha));
            }
        }
        // The blue cell: along your lane, or across to the other lane's desktop. At a lane's end
        // it gives a little with the picture's stretch.
        var from = CellAt(p, fromLane, fromIndex);
        var to = toward != null ? CellAt(p, toLane, toIndex) : CellAt(p, fromLane, fromIndex - f);
        float bx = from.X + (to.X - from.X) * (toward != null ? k : 1), by = from.Y + (to.Y - from.Y) * (toward != null ? k : 1);
        g.RoundRect(bx, by, cell, cellH, 2.5f * s, A(0xFF4CC2FF, alpha));
    }

    // After a swipe the pill stays over the desktop as long as Windows' own would (about two
    // seconds), in a small window of its own, then fades. A swipe that went back stays too.
    const double PillStays = 2.0, PillFades = 0.25;
    double _pillSince;
    Pill? _pillLingering;

    void LingerPill(SwitchState s)
    {
        var p = PillFor();
        using var pixels = _gfx.Offscreen((int)p.X, (int)p.Y, (int)p.W, (int)p.H, () => DrawPill(s, p, HudAlpha(s)));
        _map.Show(pixels, _overlay.Area.Left + (int)p.X, _overlay.Area.Top + (int)p.Y);
        // Shown before the overlay goes, so no frame has neither.
        DwmFlush();
        _pillLingering = p;
        _pillShownX = _overlay.Area.Left + p.X;
        _pillSince = s.Switched ? s.SwitchedAt : Now;
    }

    float _pillShownX;

    void StepLingeringPill(double now)
    {
        double t = now - _pillSince;
        if (t >= PillStays + PillFades) { _map.Hide(); return; }
        if (t > PillStays) _map.Fade((float)(1 - (t - PillStays) / PillFades));
        // Windows' volume or brightness pop-up comes up where the pill is: the pill moves aside.
        if (_pillLingering is not { } p) return;
        float home = _overlay.Area.Left + p.X, x = home;
        if (WindowsPill.ShownAt() is { } osd && osd.Left < home + p.W && osd.Right > home)
            x = osd.Left - 12 * _overlay.Scale - p.W;
        if (Math.Abs(x - _pillShownX) < 0.5f) return;
        _pillShownX += (x - _pillShownX) * 0.35f;
        if (Math.Abs(x - _pillShownX) < 0.5f) _pillShownX = x;
        _map.Move((int)MathF.Round(_pillShownX), _overlay.Area.Top + (int)p.Y);
    }

    static uint A(uint argb, float alpha) => ((uint)Math.Clamp((argb >> 24) * alpha, 0, 255) << 24) | (argb & 0xFFFFFF);
}
