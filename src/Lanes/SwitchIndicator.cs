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
            var r = Frame(h);
            bool visible = IsWindowVisible(h);
            bool cloaked = Cloaked(h);
            GetWindowThreadProcessId(h, out uint pid);
            if (pid == Environment.ProcessId) return true;
            string proc;
            try { using var p = Process.GetProcessById((int)pid); proc = p.ProcessName; } catch { proc = "?"; }
            long ex = GetWindowLongPtr(h, GWL_EXSTYLE).ToInt64();
            DwmGetWindowAttribute(h, DWMWA_CLOAKED, out int how, 4);
            lines.Add($"  {proc} {h} class='{ClassName(h)}' title='{WindowText(h)}' rect={r} ex=0x{ex:X}{(visible ? " visible" : "")}{(cloaked ? $" cloaked{how}" : "")}");
            return true;
        }, IntPtr.Zero);
        Log.Write($"windows {when}:\n" + string.Join("\n", lines));
    }
}
