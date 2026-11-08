namespace Lanes.Core;

// One finger on the pad. X and Y are in pad widths: X runs 0..1 left to right, Y runs
// 0..(height/width) top to bottom, so a millimetre is the same distance on both axes.
public readonly record struct Finger(uint Id, double X, double Y);

// One report from the touchpad: when it came (ms) and the fingers whose positions it carries.
public readonly record struct TouchFrame(long Time, Finger[] Fingers);

public enum GestureKind { Switch, Overview }
public enum Axis { Horizontal, Vertical }

public interface IGestureSink
{
    // Fingers moved far enough to tell what the gesture is.
    void Begin(GestureKind kind, Axis axis);
    // Travel since Begin, in pad widths; positive is right / down on the pad.
    void Move(double dx, double dy);
    // Fingers left the pad; velocity in pad widths per second over the last moments.
    void End(double vx, double vy);
}

// Turns touchpad reports into swipes. Four fingers make a Switch swipe on either axis; three
// fingers make an Overview swipe, vertical only (three fingers sideways stay with Windows).
// Travel is measured per finger by contact ID, so a finger landing late or lifting early
// does not make the picture jump.
public sealed class GestureRecognizer
{
    // How far the fingers travel before the swipe is recognised (about 4 mm on a Magic Trackpad).
    public double LockDistance = 0.025;
    // Velocity is measured over this much of the end of the swipe.
    public const long VelocityWindowMs = 80;
    // With no report for this long the fingers are taken as lifted (some drivers send no last report).
    public const long SilenceMs = 400;

    enum State { Idle, Pending, Tracking, Ignored }

    readonly IGestureSink _sink;
    State _state = State.Idle;
    readonly Dictionary<uint, (double X, double Y)> _last = new();
    double _tx, _ty, _ox, _oy;
    int _maxFingers;
    long _lastTime;
    readonly List<(long T, double X, double Y)> _samples = new();

    public GestureRecognizer(IGestureSink sink) => _sink = sink;

    public bool Tracking => _state == State.Tracking;
    public GestureKind Kind { get; private set; }
    public Axis Axis { get; private set; }

    public void OnFrame(in TouchFrame frame)
    {
        var fingers = frame.Fingers;
        int count = fingers.Length;
        _lastTime = frame.Time;

        switch (_state)
        {
            case State.Idle:
                if (count >= 3) Start(fingers);
                break;

            case State.Pending:
                Accumulate(fingers);
                _maxFingers = Math.Max(_maxFingers, count);
                if (count < 3) { _state = count == 0 ? State.Idle : State.Ignored; break; }
                if (Math.Sqrt(_tx * _tx + _ty * _ty) >= LockDistance) Lock(frame.Time);
                break;

            case State.Tracking:
                Accumulate(fingers);
                if (count <= 1)
                {
                    Finish();
                    _state = count == 0 ? State.Idle : State.Ignored;
                    break;
                }
                _samples.Add((frame.Time, _tx, _ty));
                Trim(frame.Time);
                _sink.Move(_tx - _ox, _ty - _oy);
                break;

            case State.Ignored:
                if (count == 0) _state = State.Idle;
                break;
        }
    }

    // Called by a timer: a swipe whose reports stopped is over.
    public void OnTick(long now)
    {
        if (_state == State.Idle || now - _lastTime < SilenceMs) return;
        // The fingers stopped reporting a while ago: whatever speed they had is long gone.
        if (_state == State.Tracking) { _samples.Clear(); Finish(); }
        _state = State.Idle;
        _last.Clear();
    }

    void Start(Finger[] fingers)
    {
        _state = State.Pending;
        _tx = _ty = 0;
        _maxFingers = fingers.Length;
        _last.Clear();
        foreach (var f in fingers) _last[f.Id] = (f.X, f.Y);
    }

    void Lock(long time)
    {
        Kind = _maxFingers >= 4 ? GestureKind.Switch : GestureKind.Overview;
        Axis = Math.Abs(_tx) >= Math.Abs(_ty) ? Axis.Horizontal : Axis.Vertical;
        if (Kind == GestureKind.Overview && Axis == Axis.Horizontal) { _state = State.Ignored; return; }
        _state = State.Tracking;
        _ox = _tx;
        _oy = _ty;
        _samples.Clear();
        _samples.Add((time, _tx, _ty));
        _sink.Begin(Kind, Axis);
    }

    // Adds the average move of the fingers present in both this report and the last one.
    void Accumulate(Finger[] fingers)
    {
        double dx = 0, dy = 0;
        int matched = 0;
        foreach (var f in fingers)
            if (_last.TryGetValue(f.Id, out var p))
            {
                dx += f.X - p.X;
                dy += f.Y - p.Y;
                matched++;
            }
        if (matched > 0)
        {
            _tx += dx / matched;
            _ty += dy / matched;
        }
        _last.Clear();
        foreach (var f in fingers) _last[f.Id] = (f.X, f.Y);
    }

    void Trim(long now)
    {
        int drop = 0;
        while (drop < _samples.Count - 2 && now - _samples[drop].T > VelocityWindowMs) drop++;
        _samples.RemoveRange(0, drop);
    }

    void Finish()
    {
        var (vx, vy) = Velocity();
        _sink.End(vx, vy);
        _samples.Clear();
    }

    // Least-squares slope of travel over the last samples, in pad widths per second.
    (double, double) Velocity()
    {
        if (_samples.Count < 2) return (0, 0);
        long end = _samples[^1].T;
        var s = _samples.Where(p => end - p.T <= VelocityWindowMs).ToList();
        if (s.Count < 2) return (0, 0);
        double mt = s.Average(p => (double)p.T), mx = s.Average(p => p.X), my = s.Average(p => p.Y);
        double stt = s.Sum(p => (p.T - mt) * (p.T - mt));
        if (stt <= 0) return (0, 0);
        double vx = s.Sum(p => (p.T - mt) * (p.X - mx)) / stt * 1000;
        double vy = s.Sum(p => (p.T - mt) * (p.Y - my)) / stt * 1000;
        return (vx, vy);
    }
}
