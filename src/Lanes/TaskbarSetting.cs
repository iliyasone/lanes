using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace Lanes;

// Windows' setting "Show all open windows on the taskbar: on all desktops" (Settings, System,
// Multitasking). Lanes turns it on once, so the taskbar keeps every app whatever desktop you are
// on, as the Dock does on a Mac (clicking one that is on another desktop goes there), instead of
// changing with every switch. The value you had is kept and comes back on uninstall; if you
// change the setting later, Lanes leaves your choice alone. The same for the taskbar's previews.
static class TaskbarSetting
{
    const string Key = @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced";
    const string Name = "VirtualDesktopTaskbarFilter";
    static readonly string Saved = Path.Combine(Log.Dir, "taskbar.txt");

    public static void ShowAllDesktops()
    {
        try
        {
            if (File.Exists(Saved)) return;
            using var k = Registry.CurrentUser.CreateSubKey(Key);
            object? before = k.GetValue(Name);
            Directory.CreateDirectory(Log.Dir);
            File.WriteAllText(Saved, before?.ToString() ?? "none");
            k.SetValue(Name, 0, RegistryValueKind.DWord);
            Notify();
            Log.Write($"taskbar: windows of all desktops shown (was {before ?? "not set"})");
        }
        catch (Exception e) { Log.Write($"taskbar: {e.Message}"); }
    }

    public static void Restore()
    {
        try
        {
            if (File.Exists(Saved))
            {
                string v = File.ReadAllText(Saved).Trim();
                using var k = Registry.CurrentUser.CreateSubKey(Key);
                if (v == "none") k.DeleteValue(Name, false);
                else k.SetValue(Name, int.Parse(v), RegistryValueKind.DWord);
                File.Delete(Saved);
                Notify();
                Log.Write($"taskbar: setting given back ({v})");
            }
        }
        catch (Exception e) { Log.Write($"taskbar: {e.Message}"); }
        PreviewsBack();
    }

    // Windows' small previews of an app's windows, which come up when the pointer rests on its
    // taskbar button, are turned off: Lanes shows its map of the desktops there instead (see
    // TaskbarButtons). They wait that long before coming up (some 17 minutes). The value you had
    // is kept and comes back on uninstall.
    const string Hover = "ExtendedUIHoverTime";
    const int Never = 1_000_000;
    static readonly string SavedHover = Path.Combine(Log.Dir, "taskbar-previews.txt");

    // Whether they were on until now (Explorer may still have them on until it starts again).
    public static bool PreviewsOff()
    {
        try
        {
            using var k = Registry.CurrentUser.CreateSubKey(Key);
            object? before = k.GetValue(Hover);
            if (before is int v && v == Never) return false;
            Directory.CreateDirectory(Log.Dir);
            if (!File.Exists(SavedHover)) File.WriteAllText(SavedHover, before?.ToString() ?? "none");
            k.SetValue(Hover, Never, RegistryValueKind.DWord);
            Notify();
            Log.Write($"taskbar: Windows' previews of the windows turned off (the delay was {before ?? "not set"})");
            return true;
        }
        catch (Exception e) { Log.Write($"taskbar previews: {e.Message}"); return false; }
    }

    static void PreviewsBack()
    {
        try
        {
            if (!File.Exists(SavedHover)) return;
            string v = File.ReadAllText(SavedHover).Trim();
            using var k = Registry.CurrentUser.CreateSubKey(Key);
            if (v == "none") k.DeleteValue(Hover, false);
            else k.SetValue(Hover, int.Parse(v), RegistryValueKind.DWord);
            File.Delete(SavedHover);
            Notify();
            Log.Write($"taskbar: previews given back ({v})");
        }
        catch (Exception e) { Log.Write($"taskbar previews: {e.Message}"); }
    }

    // Explorer reads the previews' delay only as it starts: it is ended, and Windows starts it
    // again by itself (here, if it has not within five seconds). Open File Explorer windows close.
    public static void RestartExplorer()
    {
        try
        {
            var tray = FindWindow("Shell_TrayWnd", null);
            if (tray == IntPtr.Zero || GetWindowThreadProcessId(tray, out uint pid) == 0) return;
            using (var p = System.Diagnostics.Process.GetProcessById((int)pid))
            {
                p.Kill();
                p.WaitForExit(5000);
            }
            long t0 = Environment.TickCount64;
            while (FindWindow("Shell_TrayWnd", null) == IntPtr.Zero && Environment.TickCount64 - t0 < 5000) Thread.Sleep(100);
            if (FindWindow("Shell_TrayWnd", null) == IntPtr.Zero) System.Diagnostics.Process.Start("explorer.exe");
            Log.Write($"Explorer restarted; taskbar back in {Environment.TickCount64 - t0} ms");
        }
        catch (Exception e) { Log.Write($"restart Explorer: {e.Message}"); }
    }

    // Explorer re-reads its taskbar settings on this broadcast, as after the Settings app changes one.
    static void Notify() => SendMessageTimeout((IntPtr)0xFFFF, 0x001A /* WM_SETTINGCHANGE */, IntPtr.Zero, "TraySettings", 2 /* SMTO_ABORTIFHUNG */, 3000, out _);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr FindWindow(string cls, string? title);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr SendMessageTimeout(IntPtr hwnd, int msg, IntPtr wParam, string lParam, int flags, int timeout, out IntPtr result);
}
