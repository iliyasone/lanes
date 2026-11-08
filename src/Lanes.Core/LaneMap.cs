using System.Text.Json;

namespace Lanes.Core;

public enum Direction { Left, Right, Up, Down }

public sealed class Lane
{
    public List<Guid> Desktops { get; set; } = new();
    // The desktop of this lane you were on last: where a swipe from another lane lands.
    public Guid? Current { get; set; }
}

// Which Windows desktop sits in which lane. Windows keeps desktops in one row; Lanes keeps its
// own rows on top of it, by desktop ID, and follows whatever Windows does: a desktop made or
// removed elsewhere is added to or dropped from the map on the next look. Lanes are listed top
// to bottom; a lane that loses its last desktop disappears.
public sealed class LaneMap
{
    public List<Lane> Lanes { get; set; } = new();

    // Desktops Lanes made itself; uninstalling removes them (their windows move to one you had).
    public List<Guid> Made { get; set; } = new();

    // A new map: the desktops you have in one lane, with an empty lane above and below for
    // Lanes to fill with a new desktop each.
    public static LaneMap Fresh(IReadOnlyList<Guid> desktops, Guid current)
    {
        var map = new LaneMap();
        map.Lanes.Add(new Lane());
        map.Lanes.Add(new Lane { Desktops = desktops.ToList(), Current = current });
        map.Lanes.Add(new Lane());
        return map;
    }

    public (int Lane, int Index)? Locate(Guid desktop)
    {
        for (int l = 0; l < Lanes.Count; l++)
        {
            int i = Lanes[l].Desktops.IndexOf(desktop);
            if (i >= 0) return (l, i);
        }
        return null;
    }

    public Guid? Neighbor(Guid from, Direction direction)
    {
        if (Locate(from) is not var (l, i)) return null;
        var lane = Lanes[l];
        switch (direction)
        {
            case Direction.Left: return i > 0 ? lane.Desktops[i - 1] : null;
            case Direction.Right: return i + 1 < lane.Desktops.Count ? lane.Desktops[i + 1] : null;
            default:
                int to = l + (direction == Direction.Up ? -1 : 1);
                if (to < 0 || to >= Lanes.Count) return null;
                return CurrentOf(to);
        }
    }

    public Guid? CurrentOf(int lane)
    {
        var l = Lanes[lane];
        if (l.Current is Guid c && l.Desktops.Contains(c)) return c;
        return l.Desktops.Count > 0 ? l.Desktops[0] : null;
    }

    // Windows switched to this desktop, by any means: it becomes its lane's current one.
    public void Visited(Guid desktop)
    {
        if (Locate(desktop) is var (l, _)) Lanes[l].Current = desktop;
    }

    // Puts a desktop into a lane (taking it out of wherever it was), at an index or at the end.
    public void Add(Guid desktop, int lane, int? index = null)
    {
        var target = Lanes[lane];
        foreach (var l in Lanes)
        {
            l.Desktops.Remove(desktop);
            if (l.Current == desktop && l != target) l.Current = null;
        }
        var list = target.Desktops;
        list.Insert(Math.Clamp(index ?? list.Count, 0, list.Count), desktop);
    }

    // A new, empty lane at this position (0 = above all others).
    public void InsertLane(int at) => Lanes.Insert(Math.Clamp(at, 0, Lanes.Count), new Lane());

    // Brings the map in line with the desktops Windows has now: desktops that are gone are
    // dropped, new ones join the lane of the desktop you are on, and lanes left empty go away.
    public void Reconcile(IReadOnlyList<Guid> system, Guid current)
    {
        var present = system.ToHashSet();
        foreach (var lane in Lanes)
        {
            lane.Desktops.RemoveAll(d => !present.Contains(d));
            if (lane.Current is Guid c && !present.Contains(c)) lane.Current = null;
        }
        Made.RemoveAll(d => !present.Contains(d));
        Lanes.RemoveAll(l => l.Desktops.Count == 0);
        if (Lanes.Count == 0) Lanes.Add(new Lane());
        int home = Locate(current)?.Lane ?? 0;
        foreach (var d in system)
            if (Locate(d) == null) Lanes[home].Desktops.Add(d);
        Visited(current);
        foreach (var lane in Lanes)
            lane.Current ??= lane.Desktops.Count > 0 ? lane.Desktops[0] : null;
    }

    public IEnumerable<int> EmptyLanes() => Enumerable.Range(0, Lanes.Count).Where(l => Lanes[l].Desktops.Count == 0);

    static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public string Save() => JsonSerializer.Serialize(this, Json);

    public static LaneMap Load(string json) => JsonSerializer.Deserialize<LaneMap>(json) ?? new LaneMap();
}
