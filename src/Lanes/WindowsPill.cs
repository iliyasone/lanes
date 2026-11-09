using System.Diagnostics;
using System.Runtime.InteropServices;
using static Lanes.Native;

namespace Lanes;

// Windows shows a "Desktop N" pill for about two seconds after every switch, above every other
// window. During its own swipes Lanes shows its pill in that place instead, so Windows' one is
// kept away: Explorer draws it in the same window as the volume and brightness pop-ups, and that
// window shows nothing while it is minimized (as HideVolumeOSD and FluentFlyout do it). It is
// minimized just before each switch Lanes makes and put back once the pill's time is over;
// should the volume change meanwhile, it comes back at once, showing the volume instead.
static class WindowsPill
{
    static IntPtr _window;
    static long _exStyle;
    static RECT _rect;
    static bool _missingLogged;

    public static bool Hidden { get; private set; }
    // Explorer has hidden the window since Lanes minimized it: the pill's time is over, and
    // whatever it shows from now on is something new.
    static bool _pillOver;

    // Lanes found the window, so it can keep Windows' pill away.
    public static bool Available => Find() != IntPtr.Zero;

    // Explorer's pop-up window: an XAML island whose input site sits where the pop-up shows (the
    // full-screen island Explorer also has keeps its input site at 0,0).
    static IntPtr Find()
    {
        if (_window != IntPtr.Zero && IsWindow(_window)) return _window;
        _window = IntPtr.Zero;
        IntPtr island = IntPtr.Zero;
        while ((island = FindWindowEx(IntPtr.Zero, island, "XamlExplorerHostIslandWindow", null)) != IntPtr.Zero)
        {
            var bridge = FindWindowEx(island, IntPtr.Zero, "Windows.UI.Composition.DesktopWindowContentBridge", "DesktopWindowXamlSource");
            var input = bridge == IntPtr.Zero ? IntPtr.Zero : FindWindowEx(bridge, IntPtr.Zero, "Windows.UI.Input.InputSite.WindowClass", null);
            if (input == IntPtr.Zero || !GetWindowRect(input, out var at) || at.Left == 0 && at.Top == 0) continue;
            if (!GetWindowRect(island, out var r) || r.Width > 800 || r.Height > 400 || !InExplorer(island)) continue;
            return _window = island;
        }
        return IntPtr.Zero;
    }

    static bool InExplorer(IntPtr hwnd)
    {
        GetWindowThreadProcessId(hwnd, out uint pid);
        try { using var p = Process.GetProcessById((int)pid); return p.ProcessName.Equals("explorer", StringComparison.OrdinalIgnoreCase); }
        catch { return false; }
    }

    public static void Hide()
    {
        if (Hidden) return;
        var w = Find();
        if (w == IntPtr.Zero)
        {
            // Explorer makes the window the first time it shows a pop-up.
            if (!_missingLogged) Log.Write("Windows' desktop pill: its window is not there yet");
            _missingLogged = true;
            return;
        }
        GetWindowRect(w, out _rect);
        _exStyle = GetWindowLongPtr(w, GWL_EXSTYLE).ToInt64();
        // See-through for clicks and not drawn even if Explorer shows it anyway, and minimized.
        SetWindowLongPtr(w, GWL_EXSTYLE, (IntPtr)(_exStyle | WS_EX_LAYERED | WS_EX_TRANSPARENT));
        ShowWindow(w, SW_SHOWMINNOACTIVE);
        Hidden = true;
        _pillOver = false;
        VolumeWatch.Watch();
    }

    // Called often while hidden: notes when Explorer hides the window, its pill's time over.
    public static void Watch()
    {
        if (Hidden && !_pillOver && _window != IntPtr.Zero && !IsWindowVisible(_window)) _pillOver = true;
    }

    // Shows the window where Explorer put it, with what Explorer shows in it now (the volume).
    public static void Reveal()
    {
        if (!Hidden) return;
        Hidden = false;
        var w = _window;
        if (w == IntPtr.Zero || !IsWindow(w)) return;
        SetWindowLongPtr(w, GWL_EXSTYLE, (IntPtr)(_exStyle & ~(long)(WS_EX_LAYERED | WS_EX_TRANSPARENT)));
        ShowWindow(w, SW_SHOWNOACTIVATE);
    }

    // Back to how Explorer keeps it, hidden and not minimized, without showing on the way: it is
    // restored off the screen, hidden, and moved back.
    public static void Restore()
    {
        if (!Hidden) return;
        // Explorer shows something new in it (the volume after the pill was gone): let it show.
        if (_pillOver && _window != IntPtr.Zero && IsWindowVisible(_window))
        {
            Reveal();
            return;
        }
        Hidden = false;
        var w = _window;
        if (w == IntPtr.Zero || !IsWindow(w)) return;
        var off = new WINDOWPLACEMENT
        {
            Length = Marshal.SizeOf<WINDOWPLACEMENT>(),
            ShowCmd = SW_SHOWNOACTIVATE,
            NormalPosition = new RECT(-32000, -32000, -32000 + _rect.Width, -32000 + _rect.Height),
        };
        SetWindowPlacement(w, ref off);
        ShowWindow(w, SW_HIDE);
        SetWindowLongPtr(w, GWL_EXSTYLE, (IntPtr)(_exStyle & ~(long)(WS_EX_LAYERED | WS_EX_TRANSPARENT)));
        SetWindowPos(w, IntPtr.Zero, _rect.Left, _rect.Top, 0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
    }

    // Where Explorer's pop-up is while it shows one (the volume, the brightness), or null.
    public static RECT? ShownAt()
    {
        var w = _window;
        if (Hidden || w == IntPtr.Zero || !IsWindowVisible(w) || IsIconic(w) || !GetWindowRect(w, out var r)) return null;
        return r;
    }

    // After Lanes stopped without putting it back (a crash): Explorer's window is still minimized.
    public static void Repair()
    {
        var w = Find();
        if (w == IntPtr.Zero || !IsIconic(w)) return;
        _exStyle = GetWindowLongPtr(w, GWL_EXSTYLE).ToInt64();
        _rect = new RECT(0, 0, 211, 76);
        Hidden = true;
        Restore();
        Log.Write("Windows' desktop pill: put back after an earlier run");
    }
}
