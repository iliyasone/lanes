using System.Diagnostics;
using static Lanes.Native;

namespace Lanes;

// Windows shows its own "Desktop N" pill after a desktop switch. This finds what draws it.
static class SwitchIndicator
{
    public static void Describe(string when)
    {
        var lines = new List<string>();
        EnumWindows((h, _) =>
        {
            if (!IsWindowVisible(h) || Cloaked(h)) return true;
            GetWindowThreadProcessId(h, out uint pid);
            string proc;
            try { using var p = Process.GetProcessById((int)pid); proc = p.ProcessName; } catch { proc = "?"; }
            if (proc is not ("explorer" or "ShellExperienceHost" or "ShellHost" or "StartMenuExperienceHost")) return true;
            var r = Frame(h);
            long ex = GetWindowLongPtr(h, GWL_EXSTYLE).ToInt64();
            lines.Add($"  {proc} {h} class='{ClassName(h)}' title='{WindowText(h)}' rect={r} ex=0x{ex:X}");
            return true;
        }, IntPtr.Zero);
        Log.Write($"shell windows {when}:\n" + string.Join("\n", lines));
    }
}
