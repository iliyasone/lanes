namespace Lanes.Core;

// Alt+Tab as Cmd+Tab on a Mac: it goes between apps, not windows. An app is its windows grouped
// as the taskbar groups them; apps come in the order they were last in front, and going to one
// brings its window that was last in front.
public sealed record SwitchApp(string Id, IReadOnlyList<IntPtr> Windows)
{
    public IntPtr Window => Windows[0];
}

public static class AppSwitch
{
    // The apps of these windows (each with its app's id, in z-order, front first): those of the
    // windows last in front first, in `recent`'s order, then the rest as they lie.
    public static List<SwitchApp> Apps(IReadOnlyList<(IntPtr Window, string Id)> windows, IReadOnlyList<IntPtr> recent)
    {
        var rank = new Dictionary<IntPtr, int>();
        for (int i = 0; i < recent.Count; i++) rank.TryAdd(recent[i], i);
        var ordered = windows.Select((w, i) => (w, Key: rank.TryGetValue(w.Window, out int r) ? r : recent.Count + i))
            .OrderBy(x => x.Key).Select(x => x.w);
        var apps = new List<(string Id, List<IntPtr> Windows)>();
        foreach (var (window, id) in ordered)
        {
            int at = apps.FindIndex(a => string.Equals(a.Id, id, StringComparison.OrdinalIgnoreCase));
            if (at < 0) apps.Add((id, new List<IntPtr> { window }));
            else apps[at].Windows.Add(window);
        }
        return apps.Select(a => new SwitchApp(a.Id, a.Windows)).ToList();
    }

    // Which app the first Tab picks: the one you were in before this one (Shift: the last one).
    public static int First(int count, bool back) => count == 0 ? -1 : back ? count - 1 : Math.Min(1, count - 1);

    // One step right (or left, `by` -1) from `at`, round from one end to the other.
    public static int Step(int at, int by, int count) => count == 0 ? -1 : ((at + by) % count + count) % count;

    // The size of each icon: `icon` as long as the row of `count` cells (an icon and `pad` round
    // it) fits in `room`, smaller when it does not.
    public static float IconSize(int count, float room, float icon, float pad) =>
        count == 0 ? icon : Math.Max(1, Math.Min(icon, room / count - 2 * pad));
}

// The windows in the order they were last in front, most recent first.
public sealed class RecentWindows
{
    readonly List<IntPtr> _order = new();

    public IReadOnlyList<IntPtr> Order => _order;

    public void Front(IntPtr window)
    {
        if (window == IntPtr.Zero) return;
        _order.Remove(window);
        _order.Insert(0, window);
    }

    // Leaves out windows no longer there.
    public void Keep(IEnumerable<IntPtr> windows)
    {
        var alive = windows.ToHashSet();
        _order.RemoveAll(w => !alive.Contains(w));
    }
}
