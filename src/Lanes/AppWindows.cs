using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using static Lanes.Native;

namespace Lanes;

sealed record AppWindow(IntPtr Handle, RECT Frame, string Title, string App);

static class AppWindows
{
    const int GW_OWNER = 4, GCLP_HICON = -14, GCLP_HICONSM = -34, WM_GETICON = 0x007F;
    [DllImport("user32.dll")] static extern IntPtr GetWindow(IntPtr hwnd, int cmd);
    [DllImport("user32.dll")] static extern IntPtr GetClassLongPtr(IntPtr hwnd, int index);
    [DllImport("user32.dll")] static extern IntPtr SendMessageTimeout(IntPtr hwnd, int msg, IntPtr wp, IntPtr lp, uint flags, uint timeout, out IntPtr result);

    static readonly HashSet<string> ShellClasses = new() { "Progman", "WorkerW", "Shell_TrayWnd", "Shell_SecondaryTrayWnd", "Windows.UI.Core.CoreWindow", "XamlExplorerHostIslandWindow" };
    static readonly Dictionary<uint, string> AppNames = new();

    // The windows you see on the current desktop on this monitor area, back to front; or, with
    // `desktop`, the windows that are on that other desktop (Windows keeps them cloaked).
    public static List<AppWindow> OnScreen(RECT area, IntPtr own, Guid? desktop = null)
    {
        var list = new List<AppWindow>();
        EnumWindows((h, _) =>
        {
            if (h == own || !IsWindowVisible(h) || IsIconic(h)) return true;
            if (desktop == null ? Cloaked(h) : !CloakedByShell(h) || Desktops.WindowDesktop(h) != desktop) return true;
            if (App(h, area) is { } w) list.Add(w);
            return true;
        }, IntPtr.Zero);
        list.Reverse(); // EnumWindows goes front to back
        return list;
    }

    // The windows on every desktop but this one, back to front, by desktop: one pass over the
    // windows instead of one per desktop.
    public static Dictionary<Guid, List<AppWindow>> OnOtherDesktops(RECT area, IntPtr own)
    {
        var map = new Dictionary<Guid, List<AppWindow>>();
        EnumWindows((h, _) =>
        {
            if (h == own || !IsWindowVisible(h) || IsIconic(h) || !CloakedByShell(h)) return true;
            if (App(h, area) is not { } w) return true;
            var d = Desktops.WindowDesktop(h);
            if (d == Guid.Empty) return true;
            if (!map.TryGetValue(d, out var list)) map[d] = list = new List<AppWindow>();
            list.Add(w);
            return true;
        }, IntPtr.Zero);
        foreach (var list in map.Values) list.Reverse();
        return map;
    }

    // The window as the overview lists it, or null for what is not an app's own window.
    static AppWindow? App(IntPtr h, RECT area)
    {
        if (!IsApp(h, out var f, out string title)) return null;
        if (f.Right <= area.Left || f.Left >= area.Right || f.Bottom <= area.Top || f.Top >= area.Bottom) return null;
        return new AppWindow(h, f, title, AppName(h));
    }

    // Whether this is an app's own window (the ones the overview shows and the desktop gives
    // shadows), with its frame and title: not a tool window, a pop-up of another window,
    // something over the apps or a part of the shell.
    public static bool IsApp(IntPtr h, out RECT frame, out string title)
    {
        frame = default;
        title = "";
        long ex = GetWindowLongPtr(h, GWL_EXSTYLE).ToInt64();
        if ((ex & WS_EX_TOOLWINDOW) != 0 && (ex & WS_EX_APPWINDOW) == 0) return false;
        if (GetWindow(h, GW_OWNER) != IntPtr.Zero && (ex & WS_EX_APPWINDOW) == 0) return false;
        // A window that never takes focus is something over the apps (a dictation pill, an
        // on-screen keyboard), not one of them.
        if ((ex & WS_EX_NOACTIVATE) != 0) return false;
        if (ShellClasses.Contains(ClassName(h))) return false;
        title = WindowText(h);
        if (title.Length == 0) return false;
        frame = Frame(h);
        return frame.Width >= 40 && frame.Height >= 40;
    }

    // "Telegram Desktop", "Google Chrome": the product name of the window's program. Store apps
    // (Settings, Photos) all run in one host program, "Application Frame Host": their windows go
    // by their titles, which are the apps' names.
    public static string AppName(IntPtr hwnd)
    {
        GetWindowThreadProcessId(hwnd, out uint pid);
        if (AppNames.TryGetValue(pid, out var name)) return name.Length > 0 ? name : WindowText(hwnd);
        name = "";
        try
        {
            using var p = Process.GetProcessById((int)pid);
            string? path = p.MainModule?.FileName;
            if (path != null && !Path.GetFileNameWithoutExtension(path).Equals("ApplicationFrameHost", StringComparison.OrdinalIgnoreCase))
            {
                var info = FileVersionInfo.GetVersionInfo(path);
                name = !string.IsNullOrWhiteSpace(info.FileDescription) ? info.FileDescription! : Path.GetFileNameWithoutExtension(path);
            }
            else if (path == null) name = p.ProcessName;
        }
        catch { }
        AppNames[pid] = name;
        return name.Length > 0 ? name : WindowText(hwnd);
    }

    // Brings a window to the front. Windows allows that after a click Lanes got, but not always
    // after a gesture (the overview closed with three fingers), so Lanes joins the input of the
    // window in front for that moment, as the desktop switch does.
    public static void Activate(IntPtr hwnd)
    {
        if (IsIconic(hwnd)) ShowWindow(hwnd, SW_RESTORE);
        uint me = GetCurrentThreadId(), front = GetWindowThreadProcessId(GetForegroundWindow(), out _);
        bool joined = front != 0 && front != me && AttachThreadInput(me, front, true);
        BringWindowToTop(hwnd);
        SetForegroundWindow(hwnd);
        if (joined) AttachThreadInput(me, front, false);
    }

    [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] static extern bool AttachThreadInput(uint from, uint to, bool attach);
    [DllImport("user32.dll")] static extern bool BringWindowToTop(IntPtr hwnd);
}
