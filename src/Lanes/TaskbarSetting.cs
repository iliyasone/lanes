using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace Lanes;

// Windows' setting "Show all open windows on the taskbar: on all desktops" (Settings, System,
// Multitasking). Lanes turns it on once, so the taskbar keeps every app whatever desktop you are
// on, as the Dock does on a Mac (clicking one that is on another desktop goes there), instead of
// changing with every switch. The value you had is kept and comes back on uninstall; if you
// change the setting later, Lanes leaves your choice alone.
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
            if (!File.Exists(Saved)) return;
            string v = File.ReadAllText(Saved).Trim();
            using var k = Registry.CurrentUser.CreateSubKey(Key);
            if (v == "none") k.DeleteValue(Name, false);
            else k.SetValue(Name, int.Parse(v), RegistryValueKind.DWord);
            File.Delete(Saved);
            Notify();
            Log.Write($"taskbar: setting given back ({v})");
        }
        catch (Exception e) { Log.Write($"taskbar: {e.Message}"); }
    }

    // Explorer re-reads its taskbar settings on this broadcast, as after the Settings app changes one.
    static void Notify() => SendMessageTimeout((IntPtr)0xFFFF, 0x001A /* WM_SETTINGCHANGE */, IntPtr.Zero, "TraySettings", 2 /* SMTO_ABORTIFHUNG */, 3000, out _);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr SendMessageTimeout(IntPtr hwnd, int msg, IntPtr wParam, string lParam, int flags, int timeout, out IntPtr result);
}
