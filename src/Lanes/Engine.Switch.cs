using Lanes.Core;
using Vortice.Direct2D1;
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
    }

    void BeginSwitch(Axis axis)
    {
        _current = Desktops.Current;
        var snap = TakeTouchCapture();
        RefreshTitles();
        if (snap.Width != _gfx.Width || snap.Height != _gfx.Height) _gfx.Attach(_overlay.Handle, snap.Width, snap.Height);
        Remember(_current, snap);
        var s = new SwitchState
        {
            Axis = axis,
            From = _current,
            Previous = _lanes.Neighbor(_current, axis == Axis.Horizontal ? Direction.Left : Direction.Up),
            Next = _lanes.Neighbor(_current, axis == Axis.Horizontal ? Direction.Right : Direction.Down),
        };
        double page = axis == Axis.Horizontal ? _gfx.Width : _gfx.Height;
        s.Track = new SwipeTrack(page, s.Previous != null, s.Next != null, _tuning);
        s.FromPicture = Picture(_current)!;
        s.PreviousPicture = s.Previous is Guid p ? Picture(p) : null;
        s.NextPicture = s.Next is Guid n ? Picture(n) : null;
        _switch = s;
        _gfx.Frames.Clear();
        RenderSwitch();
        _overlay.Show();
        Log.Write($"switch {axis} from {Title(_current)}; previous {(s.Previous is Guid a ? Title(a) : "-")}, next {(s.Next is Guid b ? Title(b) : "-")}");
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
        Log.Write($"release at {s.Offset / s.Track.Page:0.00} screen, {speed / s.Track.Page:0.00} screens/s: {outcome}");
    }

    void StepSwitch(double now)
    {
        var s = _switch!;
        if (s.Spring == null) return;
        double t = now - s.SpringStart;
        s.Offset = s.Spring.At(t);
        if (!s.Spring.Done(t)) return;
        s.Offset = s.Spring.Target;
        RenderSwitch();
        FinishSwitch();
    }

    void FinishSwitch()
    {
        var s = _switch!;
        _switch = null;
        Guid? to = s.Outcome switch { Outcome.Next => s.Next, Outcome.Previous => s.Previous, _ => null };
        if (to is Guid target)
        {
            if (!Desktops.Switch(target)) Log.Write("switch failed");
            _current = target;
            _lanes.Visited(target);
            Settings.SaveLanes(_lanes);
            // Let Windows draw the new desktop underneath before the picture of it goes away.
            DwmFlush();
            DwmFlush();
        }
        _overlay.Hide();
        Log.Write($"switch drawn: {_gfx.FrameStats()}");
    }

    void RenderSwitch()
    {
        var s = _switch!;
        var g = _gfx;
        float scale = _overlay.Scale, gap = GapDip * scale;
        float w = g.Width, h = g.Height, o = (float)s.Offset;
        bool horizontal = s.Axis == Axis.Horizontal;
        float page = horizontal ? w : h;
        g.Begin(Gfx.Color(0xFF0A0C10));
        Draw(s.FromPicture, o);
        ID2D1Bitmap1? other = o < 0 ? s.NextPicture : o > 0 ? s.PreviousPicture : null;
        float otherAt = o < 0 ? o + page + gap : o - page - gap;
        if (other != null) Draw(other, otherAt);

        // Each desktop darkens toward the edge that faces the gap, so the two read as separate sheets.
        float sh = 24 * scale;
        if (o < 0) { EdgeShadow(o + page - sh, false); if (other != null) EdgeShadow(otherAt, true); }
        if (o > 0) { EdgeShadow(o, true); if (other != null) EdgeShadow(otherAt + page - sh, false); }

        Guid? target = o < 0 ? s.Next : o > 0 ? s.Previous : null;
        if (Math.Abs(o) > 1)
        {
            float alpha = s.Spring == null ? 1 : (float)Math.Clamp(1 - (Now - s.SpringStart) / 0.18, 0, 1);
            // Pulling where there is no desktop: the pill names where you stay.
            Hud(target ?? s.From, target == null ? 0 : (float)Math.Min(1, Math.Abs(s.Offset) / page), alpha, target == null);
        }
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

    // The pill near the bottom during a swipe: the lane map with where you are and where you are
    // going, and the name of the desktop you are going to.
    void Hud(Guid target, float progress, float alpha, bool staying = false)
    {
        if (alpha <= 0) return;
        var g = _gfx;
        float s = _overlay.Scale;
        var loc = _lanes.Locate(target);
        var from = _lanes.Locate(_current);
        string name = Title(target);
        string lane = loc is var (l, _) ? $"{LaneMap.Names[l]} lane" : "";
        var nameFont = g.Font(15 * s, Vortice.DirectWrite.FontWeight.SemiBold);
        var laneFont = g.Font(13 * s);
        float nameW = g.TextWidth(name, nameFont), laneW = g.TextWidth(lane, laneFont);
        float cell = 14 * s, cellH = 9 * s, cg = 3 * s;
        int widest = Math.Max(1, _lanes.Lanes.Max(x => x.Desktops.Count));
        float mapW = widest * cell + (widest - 1) * cg, mapH = 3 * cellH + 2 * cg;
        float pillH = 56 * s, padL = 16 * s, padR = 20 * s, between = 16 * s;
        float pillW = padL + mapW + between + nameW + 6 * s + laneW + padR;
        float x = (g.Width - pillW) / 2, y = g.Height - 77 * s - pillH;

        g.Shadow(x, y, pillW, pillH, 20 * s, 24 * s, 8 * s, 0.35f * alpha);
        g.RoundRect(x, y, pillW, pillH, 20 * s, A(0xEB1C1C1E, alpha));
        g.RoundRectOutline(x + 0.5f, y + 0.5f, pillW - 1, pillH - 1, 20 * s, A(0x14FFFFFF, alpha), 1);
        float my = y + (pillH - mapH) / 2;
        for (int li = 0; li < _lanes.Lanes.Count; li++)
            for (int di = 0; di < _lanes.Lanes[li].Desktops.Count; di++)
            {
                float cx = x + padL + di * (cell + cg), cy = my + li * (cellH + cg);
                bool isFrom = from is var (fl, fi) && fl == li && fi == di;
                bool isTarget = !staying && loc is var (tl, ti) && tl == li && ti == di;
                if (isTarget)
                {
                    g.RoundRect(cx, cy, cell, cellH, 2.5f * s, A(0x384CC2FF, alpha));
                    g.RoundRect(cx, cy, cell, cellH, 2.5f * s, A(0xFF4CC2FF, alpha * progress));
                    g.RoundRectOutline(cx + 0.5f, cy + 0.5f, cell - 1, cellH - 1, 2.5f * s, A(0xFF4CC2FF, alpha), 1);
                }
                else g.RoundRect(cx, cy, cell, cellH, 2.5f * s, A(isFrom ? 0xF2FFFFFFu : 0x2EFFFFFFu, alpha));
            }
        float tx = x + padL + mapW + between;
        g.Text(name, nameFont, tx, y, nameW + 2, pillH, A(0xFFFFFFFF, alpha));
        g.Text(lane, laneFont, tx + nameW + 6 * s, y + 1 * s, laneW + 2, pillH, A(0xFFA0A4AB, alpha));
    }

    static uint A(uint argb, float alpha) => ((uint)Math.Clamp((argb >> 24) * alpha, 0, 255) << 24) | (argb & 0xFFFFFF);
}
