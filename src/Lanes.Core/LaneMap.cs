using System.Text.Json;

namespace Lanes.Core;

public enum Direction { Left, Right, Up, Down }

public sealed class Lane
{
    public string Name { get; set; } = "";
    public List<Guid> Desktops { get; set; } = new();
    // The desktop of this lane you were on last: where a swipe from another lane lands.
    public Guid? Current { get; set; }
}

// Which Windows desktop sits in which lane. Windows keeps desktops in one row; Lanes keeps
// its own three rows on top of it, by desktop ID, and follows whatever Windows does: a
// desktop made or removed elsewhere is added to or dropped from the map on the next look.
public sealed class LaneMap
{
    public const int Top = 0, Main = 1, Bottom = 2;
    public static readonly string[] Names = { "Top", "Main", "Bottom" };

    public List<Lane> Lanes { get; set; } = Names.Select(n => new Lane { Name = n }).ToList();

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

    public void Add(Guid desktop, int lane, int? index = null)
    {
        Remove(desktop);
        var list = Lanes[lane].Desktops;
        list.Insert(Math.Clamp(index ?? list.Count, 0, list.Count), desktop);
    }

    public void Remove(Guid desktop)
    {
        foreach (var lane in Lanes)
        {
            lane.Desktops.Remove(desktop);
            if (lane.Current == desktop) lane.Current = null;
        }
    }

    // Brings the map in line with the desktops Windows has now, in Windows' order: desktops
    // that are gone are dropped, new ones join the Main lane after the ones it has.
    public void Reconcile(IReadOnlyList<Guid> system, Guid current)
    {
        var present = system.ToHashSet();
        foreach (var lane in Lanes)
        {
            lane.Desktops.RemoveAll(d => !present.Contains(d));
            if (lane.Current is Guid c && !present.Contains(c)) lane.Current = null;
        }
        foreach (var d in system)
            if (Locate(d) == null) Lanes[Main].Desktops.Add(d);
        Visited(current);
        foreach (var lane in Lanes)
            lane.Current ??= lane.Desktops.Count > 0 ? lane.Desktops[0] : null;
    }

    public IEnumerable<int> EmptyLanes() => Enumerable.Range(0, Lanes.Count).Where(l => Lanes[l].Desktops.Count == 0);

    static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public string Save() => JsonSerializer.Serialize(this, Json);

    public static LaneMap Load(string json)
    {
        var map = JsonSerializer.Deserialize<LaneMap>(json) ?? new LaneMap();
        while (map.Lanes.Count < Names.Length) map.Lanes.Add(new Lane { Name = Names[map.Lanes.Count] });
        if (map.Lanes.Count > Names.Length) map.Lanes.RemoveRange(Names.Length, map.Lanes.Count - Names.Length);
        return map;
    }
}
