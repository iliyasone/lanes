using Lanes.Core;

namespace Lanes;

// Work that must run in the signed-in user's own session without elevation: telling Explorer to
// reload touchpad settings, and on uninstall removing the desktops Lanes made. An elevated or
// remote (SSH) Lanes asks for it through the "Lanes session" task and a request file.
static class SessionHelper
{
    static readonly string Request = Path.Combine(Log.Dir, "session-request.txt");

    public static bool Ask(string what, int waitMs)
    {
        try
        {
            Directory.CreateDirectory(Log.Dir);
            File.WriteAllText(Request, what);
            if (!Install.RunSessionTask()) return false;
            for (int t = 0; t < waitMs; t += 200)
            {
                Thread.Sleep(200);
                if (!File.Exists(Request)) return true;
            }
        }
        catch (Exception e) { Log.Write($"session helper: {e.Message}"); }
        return false;
    }

    // "Lanes.exe --session": does what the request file asks, then deletes it.
    public static int Run()
    {
        string what = File.Exists(Request) ? File.ReadAllText(Request).Trim() : "notify";
        try
        {
            if (what == "restore") RemoveMadeDesktops();
            Log.Write(TouchpadNotify.Notify(out var why) ? $"session helper ({what}): Explorer told to reload touchpad settings" : $"session helper ({what}): telling Explorer failed ({why})");
        }
        catch (Exception e) { Log.Write($"session helper: {e}"); }
        try { File.Delete(Request); } catch { }
        return 0;
    }

    // Removes the desktops Lanes made; their windows go to the first desktop you had.
    public static void RemoveMadeDesktops()
    {
        if (!Desktops.Load() || Settings.LoadLanes() is not { } map) return;
        var all = Desktops.All();
        var keep = all.FirstOrDefault(d => !map.Made.Contains(d));
        if (keep == Guid.Empty) return;
        foreach (var d in map.Made.Where(all.Contains))
        {
            Desktops.Remove(d, keep);
            Log.Write($"removed desktop {d} that Lanes made");
        }
    }
}
