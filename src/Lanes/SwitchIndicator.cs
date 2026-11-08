using System.Diagnostics;
using static Lanes.Native;

namespace Lanes;

// Windows shows its own "Desktop N" pill after a desktop switch. This finds what draws it.
static class SwitchIndicator
{
    public static void Describe(string when)
    {
        var lines = new List<string>();
        var mi = PrimaryMonitor();
        EnumWindows((h, _) =>
        {
            if (!IsWindowVisible(h)) return true;
            var r = Frame(h);
            // Small, in the lower middle of the screen.
            bool lower = r.Top > mi.Monitor.Height / 2 && r.Width < mi.Monitor.Width * 0.6 && r.Height < 220;
            bool cloaked = Cloaked(h);
            if (!lower && cloaked) return true;
            GetWindowThreadProcessId(h, out uint pid);
            string proc;
            try { using var p = Process.GetProcessById((int)pid); proc = p.ProcessName; } catch { proc = "?"; }
            if (!lower && proc != "explorer") return true;
            long ex = GetWindowLongPtr(h, GWL_EXSTYLE).ToInt64();
            lines.Add($"  {proc} {h} class='{ClassName(h)}' title='{WindowText(h)}' rect={r} ex=0x{ex:X}{(cloaked ? " cloaked" : "")}");
            return true;
        }, IntPtr.Zero);
        Log.Write($"windows {when}:\n" + string.Join("\n", lines));
    }
}
