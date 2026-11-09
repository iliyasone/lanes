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
        long ex = GetWindowLongPtr(h, GWL_EXSTYLE).ToInt64();
        if ((ex & WS_EX_TOOLWINDOW) != 0 && (ex & WS_EX_APPWINDOW) == 0) return null;
        if (GetWindow(h, GW_OWNER) != IntPtr.Zero && (ex & WS_EX_APPWINDOW) == 0) return null;
        // A window that never takes focus is something over the apps (a dictation pill, an
        // on-screen keyboard), not one of them.
        if ((ex & WS_EX_NOACTIVATE) != 0) return null;
        if (ShellClasses.Contains(ClassName(h))) return null;
        string title = WindowText(h);
        if (title.Length == 0) return null;
        var f = Frame(h);
        if (f.Width < 40 || f.Height < 40) return null;
        if (f.Right <= area.Left || f.Left >= area.Right || f.Bottom <= area.Top || f.Top >= area.Bottom) return null;
        return new AppWindow(h, f, title, AppName(h));
    }

    // "Telegram Desktop", "Google Chrome": the product name of the window's program.
    public static string AppName(IntPtr hwnd)
    {
        GetWindowThreadProcessId(hwnd, out uint pid);
        if (AppNames.TryGetValue(pid, out var name)) return name;
        name = "";
        try
        {
            using var p = Process.GetProcessById((int)pid);
            string? path = p.MainModule?.FileName;
            if (path != null)
            {
                var info = FileVersionInfo.GetVersionInfo(path);
                name = !string.IsNullOrWhiteSpace(info.FileDescription) ? info.FileDescription! : Path.GetFileNameWithoutExtension(path);
            }
            else name = p.ProcessName;
        }
        catch { }
        if (name.Length == 0 || name == "ApplicationFrameHost") name = WindowText(hwnd);
        return AppNames[pid] = name;
    }

    public static Pixels? Icon(IntPtr hwnd, int size)
    {
        IntPtr h = IntPtr.Zero;
        SendMessageTimeout(hwnd, WM_GETICON, (IntPtr)2 /* ICON_SMALL2 */, IntPtr.Zero, 2, 50, out h);
        if (h == IntPtr.Zero) SendMessageTimeout(hwnd, WM_GETICON, (IntPtr)1 /* ICON_BIG */, IntPtr.Zero, 2, 50, out h);
        if (h == IntPtr.Zero) h = GetClassLongPtr(hwnd, GCLP_HICONSM);
        if (h == IntPtr.Zero) h = GetClassLongPtr(hwnd, GCLP_HICON);
        if (h == IntPtr.Zero) return null;
        try
        {
            using var icon = System.Drawing.Icon.FromHandle(h);
            using var src = icon.ToBitmap();
            using var bmp = new Bitmap(size, size, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
            using (var g = Graphics.FromImage(bmp))
            {
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                g.DrawImage(src, 0, 0, size, size);
            }
            var p = new Pixels(size, size, opaque: false);
            var data = bmp.LockBits(new Rectangle(0, 0, size, size), System.Drawing.Imaging.ImageLockMode.ReadOnly, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
            unsafe
            {
                for (int y = 0; y < size; y++)
                    Buffer.MemoryCopy((void*)(data.Scan0 + y * data.Stride), (void*)(p.Data + y * size * 4), size * 4, size * 4);
            }
            bmp.UnlockBits(data);
            return p;
        }
        catch { return null; }
    }

    // Brings a window to the front; Windows allows it because Lanes just got the click.
    public static void Activate(IntPtr hwnd)
    {
        if (IsIconic(hwnd)) ShowWindow(hwnd, SW_RESTORE);
        SetForegroundWindow(hwnd);
    }
}
