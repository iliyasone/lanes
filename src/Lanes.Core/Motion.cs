namespace Lanes.Core;

// A damped spring toward a target, solved in closed form so any frame time gives the exact
// position: frames that come late do not change the path. Response is the period of the
// undamped spring in seconds; damping 1 settles without overshoot.
public sealed class Spring
{
    readonly double _target, _w, _z;
    readonly double _a, _b, _wd;

    public Spring(double from, double velocity, double target, double response = 0.32, double damping = 1.0)
    {
        _target = target;
        _w = 2 * Math.PI / response;
        _z = damping;
        double x0 = from - target;
        if (_z >= 1)
        {
            // Critically damped: x(t) = (a + b t) e^(-w t).
            _a = x0;
            _b = velocity + _w * x0;
        }
        else
        {
            _wd = _w * Math.Sqrt(1 - _z * _z);
            _a = x0;
            _b = (velocity + _z * _w * x0) / _wd;
        }
    }

    public double Target => _target;

    public double At(double t)
    {
        if (_z >= 1) return _target + (_a + _b * t) * Math.Exp(-_w * t);
        return _target + Math.Exp(-_z * _w * t) * (_a * Math.Cos(_wd * t) + _b * Math.Sin(_wd * t));
    }

    public double VelocityAt(double t, double dt = 1e-4) => (At(t + dt) - At(t)) / dt;

    // Settled once within half a pixel (or the given tolerance) and nearly still.
    public bool Done(double t, double tolerance = 0.5) =>
        Math.Abs(At(t) - _target) < tolerance && Math.Abs(VelocityAt(t)) < tolerance * 10;
}

public sealed class Tuning
{
    // Finger travel, in pad widths, that moves the picture by a whole screen.
    public double FullSwipe = 0.37;
    // Finger travel, in pad widths, that opens the overview fully.
    public double FullOverview = 0.28;
    // A release this fast (screens per second, toward a neighbour) switches even when short.
    public double FlickSpeed = 0.7;
    // How far, as a share of the screen, the picture stretches where there is no neighbour.
    public double RubberBand = 0.08;
    public double SwitchResponse = 0.34;
    public double OverviewResponse = 0.36;
}

public enum Outcome { Stay, Previous, Next }

// One four-finger swipe along one axis. Offset is how far the current desktop has moved,
// in pixels: negative shows the next desktop (right / below), positive the previous one
// (left / above), so the picture moves with the fingers.
public sealed class SwipeTrack
{
    readonly double _page;
    readonly bool _hasPrevious, _hasNext;
    readonly Tuning _t;

    public SwipeTrack(double page, bool hasPrevious, bool hasNext, Tuning tuning)
    {
        _page = page;
        _hasPrevious = hasPrevious;
        _hasNext = hasNext;
        _t = tuning;
    }

    public double Page => _page;

    public double OffsetFor(double travel)
    {
        double raw = travel / _t.FullSwipe * _page;
        bool allowed = raw > 0 ? _hasPrevious : _hasNext;
        if (allowed) return Math.Clamp(raw, -_page, _page);
        double r = _t.RubberBand * _page;
        return Math.Sign(raw) * r * (1 - 1 / (1 + Math.Abs(raw) / r));
    }

    public double SpeedFor(double travelSpeed) => travelSpeed / _t.FullSwipe * _page;

    // Where the picture goes when the fingers lift, given its offset and speed (px/s).
    public (Outcome Outcome, double Target) Release(double offset, double speed)
    {
        double flick = _t.FlickSpeed * _page;
        if (_hasNext && offset < 0 && (speed < -flick || (offset < -_page / 2 && speed < flick)))
            return (Outcome.Next, -_page);
        if (_hasPrevious && offset > 0 && (speed > flick || (offset > _page / 2 && speed > -flick)))
            return (Outcome.Previous, _page);
        return (Outcome.Stay, 0);
    }
}

// The three-finger overview swipe. Progress 0 is the desktop as it is, 1 the overview open.
public sealed class OverviewTrack
{
    readonly double _from;
    readonly Tuning _t;

    // `from` is the progress the overview is at when the fingers start: 0 closed, 1 open.
    public OverviewTrack(double from, Tuning tuning)
    {
        _from = from;
        _t = tuning;
    }

    // Fingers moving up (negative travel) open it; moving down close it.
    public double ProgressFor(double travelY)
    {
        double p = _from - travelY / _t.FullOverview;
        if (p > 1) return 1 + 0.06 * (1 - 1 / (1 + (p - 1) / 0.06));
        if (p < 0) return -0.04 * (1 - 1 / (1 + -p / 0.04));
        return p;
    }

    public double SpeedFor(double travelSpeedY) => -travelSpeedY / _t.FullOverview;

    public bool Release(double progress, double speed)
    {
        const double flick = 1.5;
        if (speed > flick) return true;
        if (speed < -flick) return false;
        return progress > 0.5;
    }
}
