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
        // Windows' own "Desktop N" pill was still on screen when this swipe began.
        public bool PillWasUp;
    }

    // When Windows last switched desktops; its "Desktop N" pill stays about two seconds.
    double _lastSwitchAt = -10;

    void BeginSwitch(Axis axis)
    {
        ID2D1Bitmap1? from = null;
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
            Start = Now,
            PillWasUp = Now - _lastSwitchAt < 1.2,
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
        _switch = s;
        _gfx.Frames.Clear();
        if (!_overlay.Shown)
        {
            _overlay.Place();
            RenderSwitch();
            _overlay.Show();
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

        // Each desktop carries its own "Desktop N" pill, where Windows shows it on that desktop,
        // and the lane map stays over both.
        float hud = HudAlpha(s);
        DesktopPill(s.From, o, s.PillWasUp ? 1 : hud);
        if (other != null && (o < 0 ? s.Next : s.Previous) is Guid otherDesktop) DesktopPill(otherDesktop, otherAt, 1);
        if (hud > 0.002f) LaneMapHud(s, hud);
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

    // Lanes' own pieces over a swipe come in quickly as it starts, and go as it lands.
    float HudAlpha(SwitchState s)
    {
        float a = (float)Math.Clamp((Now - s.Start) / 0.12, 0, 1);
        if (s.Spring == null) return a;
        if (s.Outcome == Outcome.Stay) return a * (float)Math.Clamp(1 - (Now - s.SpringStart) / 0.25, 0, 1);
        return a * (float)Math.Clamp(Math.Abs(s.Offset - s.Spring.Target) / s.Track.Page / 0.2, 0, 1);
    }

    // A copy of the pill Windows shows after a switch, in the same place and the same look,
    // drawn on the picture of each desktop and moving with it: the name lives in its desktop.
    // When Windows switches, its own pill comes up over this one and stays after the picture goes.
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

    // The lanes in miniature, above where the pill is: a blue cell is where you are, your lane
    // in light grey, the others darker. It stays put over the moving desktops and follows the
    // fingers: a sideways swipe slides your lane under the blue cell, one up or down carries the
    // blue cell to the next lane.
    void LaneMapHud(SwitchState sw, float alpha)
    {
        var g = _gfx;
        float s = _overlay.Scale;
        if (_lanes.Locate(sw.From) is not var (fromLane, fromIndex)) return;
        int n = _lanes.Lanes.Count;
        var anchor = new int[n];
        for (int l = 0; l < n; l++)
            anchor[l] = l == fromLane ? fromIndex : Math.Max(0, _lanes.Lanes[l].Desktops.IndexOf(_lanes.CurrentOf(l) ?? Guid.Empty));
        int lead = anchor.Max();
        int widest = Enumerable.Range(0, n).Max(l => lead - anchor[l] + _lanes.Lanes[l].Desktops.Count);
        float cell = 14 * s, cellH = 9 * s, cg = 3 * s, pad = 10 * s;
        float mapW = widest * cell + (widest - 1) * cg, mapH = n * cellH + (n - 1) * cg;
        float pw = mapW + 2 * pad, ph = mapH + 2 * pad;
        float px = MathF.Round((g.Width - pw) / 2), py = MathF.Round(WorkBottom - 77 * s - ph);
        g.RoundRect(px, py, pw, ph, 8 * s, A(0x332C2C2C, alpha));
        g.RoundRect(px + 1, py + 1, pw - 2, ph - 2, 7 * s, A(0xF22C2C2C, alpha));

        float f = (float)Math.Clamp(sw.Offset / sw.Track.Page, -1, 1);
        float slide = sw.Axis == Axis.Horizontal ? f * (cell + cg) : 0;
        float x0 = px + pad, y0 = py + pad;
        g.Clip(px + pad / 2, py, pw - pad, ph);
        for (int l = 0; l < n; l++)
            for (int i = 0; i < _lanes.Lanes[l].Desktops.Count; i++)
            {
                float cx = x0 + (lead - anchor[l] + i) * (cell + cg) + (l == fromLane ? slide : 0), cy = y0 + l * (cellH + cg);
                g.RoundRect(cx, cy, cell, cellH, 2.5f * s, A(l == fromLane ? 0x59FFFFFFu : 0x24FFFFFFu, alpha));
            }
        g.Unclip();
        float mx = x0 + lead * (cell + cg), my = y0 + fromLane * (cellH + cg);
        Guid? toward = f < 0 ? sw.Next : f > 0 ? sw.Previous : null;
        if (sw.Axis == Axis.Vertical && toward is Guid t && _lanes.Locate(t) is var (tl, _))
            my += (tl - fromLane) * (cellH + cg) * Math.Abs(f);
        g.RoundRect(mx, my, cell, cellH, 2.5f * s, A(0xFF4CC2FF, alpha));
    }

    static uint A(uint argb, float alpha) => ((uint)Math.Clamp((argb >> 24) * alpha, 0, 255) << 24) | (argb & 0xFFFFFF);
}
