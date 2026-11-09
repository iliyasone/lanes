using System.Diagnostics;
using System.Drawing;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Lanes.Core;
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
    static readonly ConcurrentDictionary<uint, string> AppNames = new();

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

    // Windows put in full screen with the overview's button, as with the green button on a Mac:
    // without their frame, over the whole monitor and the taskbar, the way Chrome goes full
    // screen. Each goes to a desktop of its own next to the one it was on (`Desk`, made by the
    // engine; `Home` is where it was), as a full-screen app has a Space of its own on a Mac. What
    // each had before (styles, place, maximized) is kept in a file with those, so it comes back
    // even after Lanes restarted.
    static readonly string FullFile = Path.Combine(Log.Dir, "full-screen.txt");
    static Dictionary<IntPtr, (long Style, long Ex, RECT Rect, bool Max, Guid Home, Guid Desk)>? _full;

    static Dictionary<IntPtr, (long Style, long Ex, RECT Rect, bool Max, Guid Home, Guid Desk)> Full
    {
        get
        {
            if (_full != null) return _full;
            _full = new();
            try
            {
                if (File.Exists(FullFile))
                    foreach (var line in File.ReadAllLines(FullFile))
                    {
                        var f = line.Split(' ');
                        var v = f.Take(8).Select(long.Parse).ToArray();
                        _full[(IntPtr)v[0]] = (v[1], v[2], new RECT((int)v[3], (int)v[4], (int)v[5], (int)v[6]), v[7] != 0,
                            f.Length > 9 ? Guid.Parse(f[8]) : Guid.Empty, f.Length > 9 ? Guid.Parse(f[9]) : Guid.Empty);
                    }
            }
            catch (Exception e) { Log.Write($"full-screen windows: {e.Message}"); }
            return _full;
        }
    }

    static void SaveFull()
    {
        try { File.WriteAllLines(FullFile, Full.Select(f => $"{(long)f.Key} {f.Value.Style} {f.Value.Ex} {f.Value.Rect.Left} {f.Value.Rect.Top} {f.Value.Rect.Right} {f.Value.Rect.Bottom} {(f.Value.Max ? 1 : 0)} {f.Value.Home} {f.Value.Desk}")); }
        catch (Exception e) { Log.Write($"full-screen windows: {e.Message}"); }
    }

    public static bool InFullScreen(IntPtr hwnd) => Full.ContainsKey(hwnd);

    const long WS_CAPTION = 0x00C00000, WS_THICKFRAME = 0x00040000, FrameEx = 0x1 | 0x100 | 0x200 | 0x20000; // dialog, window, client and static edges
    const uint SWP_FRAMECHANGED = 0x20, SMTO_ABORTIFHUNG = 2;

    // The full-screen windows that have closed, with where each was and the desktop made for it;
    // `Forget` once that desktop is dealt with.
    public static List<(IntPtr Window, Guid Home, Guid Desk)> FullScreenGone() =>
        Full.Where(f => !IsWindow(f.Key)).Select(f => (f.Key, f.Value.Home, f.Value.Desk)).ToList();

    public static void Forget(IntPtr hwnd)
    {
        if (Full.Remove(hwnd)) SaveFull();
    }

    public static void FullScreen(IntPtr hwnd, Guid home = default, Guid desk = default)
    {
        bool max = IsZoomed(hwnd);
        if (max) SendMessageTimeout(hwnd, WM_SYSCOMMAND, (IntPtr)SC_RESTORE, IntPtr.Zero, SMTO_ABORTIFHUNG, 1000, out _);
        GetWindowRect(hwnd, out var r);
        long style = GetWindowLongPtr(hwnd, GWL_STYLE).ToInt64(), ex = GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64();
        Full[hwnd] = (style, ex, r, max, home, desk);
        SaveFull();
        SetWindowLongPtr(hwnd, GWL_STYLE, (IntPtr)(style & ~(WS_CAPTION | WS_THICKFRAME)));
        SetWindowLongPtr(hwnd, GWL_EXSTYLE, (IntPtr)(ex & ~FrameEx));
        var m = MonitorOf(MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST)).Monitor;
        SetWindowPos(hwnd, IntPtr.Zero, m.Left, m.Top, m.Width, m.Height, SWP_NOZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED);
    }

    // Back as it was; hands back where it was before and the desktop made for it.
    public static (Guid Home, Guid Desk) LeaveFullScreen(IntPtr hwnd)
    {
        if (!Full.Remove(hwnd, out var was)) return default;
        SaveFull();
        SetWindowLongPtr(hwnd, GWL_STYLE, (IntPtr)was.Style);
        SetWindowLongPtr(hwnd, GWL_EXSTYLE, (IntPtr)was.Ex);
        SetWindowPos(hwnd, IntPtr.Zero, was.Rect.Left, was.Rect.Top, was.Rect.Width, was.Rect.Height, SWP_NOZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED);
        if (was.Max) SendMessageTimeout(hwnd, WM_SYSCOMMAND, (IntPtr)SC_MAXIMIZE, IntPtr.Zero, SMTO_ABORTIFHUNG, 1000, out _);
        return (was.Home, was.Desk);
    }

    // Two windows change places, each keeping its size (see Spread.Trade). A maximized one stays
    // as it is, and the other goes to the middle of the screen.
    public static void Swap(IntPtr a, IntPtr b)
    {
        var (fa, fb) = (Frame(a), Frame(b));
        static Box BoxOf(RECT r) => new(r.Left, r.Top, r.Width, r.Height);
        var (ta, _) = Spread.Trade(BoxOf(fa), BoxOf(fb), BoxOf(MonitorOf(MonitorFromWindow(b, MONITOR_DEFAULTTONEAREST)).Work));
        var (tb, _) = Spread.Trade(BoxOf(fb), BoxOf(fa), BoxOf(MonitorOf(MonitorFromWindow(a, MONITOR_DEFAULTTONEAREST)).Work));
        MoveFrame(a, fa, ta);
        MoveFrame(b, fb, tb);
    }

    // Moves a window so that its visible frame `from` is at `to`, its size as it is.
    static void MoveFrame(IntPtr hwnd, RECT from, Box to)
    {
        if (IsZoomed(hwnd) || !GetWindowRect(hwnd, out var r)) return;
        const uint flags = 0x0001 | 0x0004 | 0x0010; // SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE
        SetWindowPos(hwnd, IntPtr.Zero, (int)Math.Round(to.X) + r.Left - from.Left, (int)Math.Round(to.Y) + r.Top - from.Top, 0, 0, flags);
    }

    [DllImport("user32.dll")] static extern bool IsZoomed(IntPtr hwnd);
    [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] static extern bool AttachThreadInput(uint from, uint to, bool attach);
    [DllImport("user32.dll")] static extern bool BringWindowToTop(IntPtr hwnd);
}
