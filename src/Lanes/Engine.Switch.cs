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
        // The map columns this swipe can need, around the blue cell (see PillFor).
        public int Lo, Hi;
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
        if (!_overlay.Shown) UseArea(FullScreen(s.From) || FullScreen(s.Previous) || FullScreen(s.Next));
        // A whole step is a screen plus the gap between screens, so a finished swipe ends exactly in place.
        double page = (axis == Axis.Horizontal ? _gfx.Width : _gfx.Height) + GapDip * _overlay.Scale;
        s.Track = new SwipeTrack(page, s.Previous != null, s.Next != null, _tuning);
        s.FromPicture = from ?? Picture(_current)!;
        s.PreviousPicture = s.Previous is Guid p ? Picture(p) : null;
        s.NextPicture = s.Next is Guid n ? Picture(n) : null;
        (s.Lo, s.Hi) = MapColumns(s);
        _switch = s;
        if (!pillUp) _pillShown = null;
        _gfx.Frames.Clear();
        if (!_overlay.Shown)
        {
            _overlay.Place();
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
        if (s.Switched) LingerPill(s);
        _overlay.Hide();
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

        // Each desktop darkens toward the edge that faces the gap, so the two read as separate sheets.
        float sh = 24 * scale;
        if (o < 0) { EdgeShadow(o + page - sh, false); if (other != null) EdgeShadow(otherAt, true); }
        if (o > 0) { EdgeShadow(o, true); if (other != null) EdgeShadow(otherAt + page - sh, false); }

        // Lanes' pill stays put over both desktops.
        var pill = Glide(PillFor(s));
        float hud = HudAlpha(s);
        if (hud > 0.002f) DrawPill(s, pill, hud);
        g.End();

        void Draw(ID2D1Bitmap1 bmp, float at)
        {
            if (horizontal) g.Image(bmp, at, 0, w, h);
            else g.Image(bmp, 0, at, w, h);
        }

        // A strip `sh` wide starting at `at`; dark at its start when `darkFirst`, else at its end.
        void EdgeShadow(float at, bool darkFirst)
        {
            if (horizontal) g.Fade(at, 0, sh, h, true, darkFirst, 0.35f);
            else g.Fade(0, at, w, sh, false, darkFirst, 0.35f);
        }
    }

    // Lanes' pill comes in quickly as a swipe starts. After a switch it stays (see LingerPill); a
    // swipe that goes back to where it started takes it away as it settles.
    float HudAlpha(SwitchState s)
    {
        float a = (float)Math.Clamp((Now - s.Start) / 0.12, 0, 1);
        if (s.Spring == null || s.Outcome != Outcome.Stay) return a;
        return a * (float)Math.Clamp(1 - (Now - s.SpringStart) / 0.25, 0, 1);
    }

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

    // Lanes' pill, in the place of Windows' "Desktop N" one: the lanes in miniature, then the
    // name of the desktop you are on. In the map a blue cell is where you are, your lane is light
    // grey and the others darker. The pill stays put over the moving desktops and its map follows
    // the fingers: a sideways swipe slides your lane under the blue cell, one up or down carries
    // the blue cell to the next lane. The name changes when Windows switches. Should Windows' pill
    // still come up (Lanes could not find its window), the map goes above it, without the name.
    sealed record Pill(float X, float Y, float W, float H, float Lo, string Name);

    const float Cell = 14, CellH = 9, CellGap = 3;

    // Which desktop of lane `l` sits under the blue cell's column: the one you are on in your
    // lane, the one each other lane would take you to.
    int Anchor(int l, int fromLane, int fromIndex) =>
        l == fromLane ? fromIndex : Math.Max(0, _lanes.Lanes[l].Desktops.IndexOf(_lanes.CurrentOf(l) ?? Guid.Empty));

    // The map's columns this swipe can use, counted from the blue cell's: every lane as it lies,
    // and your lane also one step to either side the swipe can take it. Worked out once per swipe,
    // so the pill keeps its size while the lane slides and nothing is cut off at its ends.
    (int Lo, int Hi) MapColumns(SwitchState sw)
    {
        if (_lanes.Locate(sw.From) is not var (fromLane, fromIndex)) return (0, 0);
        int lo = 0, hi = 0;
        for (int l = 0; l < _lanes.Lanes.Count; l++)
        {
            int a = Anchor(l, fromLane, fromIndex), first = -a, last = _lanes.Lanes[l].Desktops.Count - 1 - a;
            if (l == fromLane && sw.Axis == Axis.Horizontal)
            {
                if (sw.Next != null) first--;
                if (sw.Previous != null) last++;
            }
            lo = Math.Min(lo, first);
            hi = Math.Max(hi, last);
        }
        return (lo, hi);
    }

    float MapHeight(float s) { int n = _lanes.Lanes.Count; return (n * CellH + (n - 1) * CellGap) * s; }

    IDWriteTextFormat PillFont() => _gfx.Font(14 * _overlay.Scale, FontWeight.Normal, family: "Segoe UI Variable Text");

    Pill PillFor(SwitchState sw)
    {
        var g = _gfx;
        float s = _overlay.Scale;
        int cols = sw.Hi - sw.Lo + 1;
        float mapW = (cols * Cell + (cols - 1) * CellGap) * s, mapH = MapHeight(s);
        if (!WindowsPill.Hidden)
        {
            float pw = mapW + 20 * s, ph = mapH + 20 * s;
            return new Pill((g.Width - pw) / 2, MathF.Round(WorkBottom - 77 * s - ph), pw, ph, sw.Lo, "");
        }
        Guid? to = sw.Outcome == Outcome.Next ? sw.Next : sw.Outcome == Outcome.Previous ? sw.Previous : null;
        string name = WindowsName(sw.Switched && to is Guid t ? t : sw.From);
        float w = 16 * s + mapW + 14 * s + g.TextWidth(name, PillFont()) + 20 * s, h = MathF.Max(48 * s, mapH + 18 * s);
        return new Pill((g.Width - w) / 2, MathF.Round(WorkBottom - 13 * s - h), w, h, sw.Lo, name);
    }

    // Where the pill is drawn: it glides to a new size and place (a longer name, a swipe that needs
    // more columns) instead of jumping.
    sealed class PillShown { public float X, W, Lo; public double At; }
    PillShown? _pillShown;

    Pill Glide(Pill to)
    {
        double now = Now;
        if (_pillShown is not { } p) _pillShown = p = new PillShown { X = to.X, W = to.W, Lo = to.Lo, At = now };
        float k = 1 - MathF.Exp(-(float)(now - p.At) / 0.06f);
        p.X += (to.X - p.X) * k;
        p.W += (to.W - p.W) * k;
        p.Lo += (to.Lo - p.Lo) * k;
        p.At = now;
        _pillDrawn = to with { X = p.X, W = p.W, Lo = p.Lo };
        return _pillDrawn;
    }

    Pill? _pillDrawn;

    void DrawPill(SwitchState sw, Pill p, float alpha)
    {
        var g = _gfx;
        float s = _overlay.Scale;
        g.RoundRect(p.X, p.Y, p.W, p.H, 8 * s, A(0x332C2C2C, alpha));
        g.RoundRect(p.X + 1, p.Y + 1, p.W - 2, p.H - 2, 7 * s, A(0xF52C2C2C, alpha));
        if (_lanes.Locate(sw.From) is not var (fromLane, fromIndex)) return;
        float cell = Cell * s, cellH = CellH * s, step = (Cell + CellGap) * s, row = (CellH + CellGap) * s;
        float x0 = p.X + (p.Name.Length > 0 ? 16 : 10) * s - p.Lo * step, y0 = MathF.Round(p.Y + (p.H - MapHeight(s)) / 2);
        float f = (float)Math.Clamp(sw.Offset / sw.Track.Page, -1, 1);
        float slide = sw.Axis == Axis.Horizontal ? f : 0;
        for (int l = 0; l < _lanes.Lanes.Count; l++)
        {
            int a = Anchor(l, fromLane, fromIndex);
            for (int i = 0; i < _lanes.Lanes[l].Desktops.Count; i++)
                g.RoundRect(x0 + (i - a + (l == fromLane ? slide : 0)) * step, y0 + l * row, cell, cellH, 2.5f * s,
                    A(l == fromLane ? 0x59FFFFFFu : 0x24FFFFFFu, alpha));
        }
        float my = y0 + fromLane * row;
        Guid? toward = f < 0 ? sw.Next : f > 0 ? sw.Previous : null;
        if (sw.Axis == Axis.Vertical && toward is Guid t && _lanes.Locate(t) is var (tl, _))
            my += (tl - fromLane) * row * Math.Abs(f);
        g.RoundRect(x0, my, cell, cellH, 2.5f * s, A(0xFF4CC2FF, alpha));
        if (p.Name.Length > 0)
        {
            var font = PillFont();
            float tw = g.TextWidth(p.Name, font);
            g.Text(p.Name, font, p.X + p.W - 20 * s - tw, p.Y, tw + 1, p.H, A(0xFFFFFFFF, alpha));
        }
    }

    // After a switch the pill stays over the desktop as long as Windows' own would (about two
    // seconds), in a small window of its own, then fades.
    const double PillStays = 2.0, PillFades = 0.25;
    double _pillSince;

    void LingerPill(SwitchState s)
    {
        if (_pillDrawn is not { } p) return;
        int x = (int)MathF.Floor(p.X) - 1, y = (int)MathF.Floor(p.Y) - 1;
        int w = (int)MathF.Ceiling(p.X + p.W) + 1 - x, h = (int)MathF.Ceiling(p.Y + p.H) + 1 - y;
        using var pixels = _gfx.Offscreen(x, y, w, h, () => DrawPill(s, p, HudAlpha(s)));
        _map.Show(pixels, _overlay.Area.Left + x, _overlay.Area.Top + y);
        _pillSince = s.SwitchedAt;
    }

    void StepLingeringPill(double now)
    {
        double t = now - _pillSince;
        if (t >= PillStays + PillFades) _map.Hide();
        else if (t > PillStays) _map.Fade((float)(1 - (t - PillStays) / PillFades));
    }

    static uint A(uint argb, float alpha) => ((uint)Math.Clamp((argb >> 24) * alpha, 0, 255) << 24) | (argb & 0xFFFFFF);
}
