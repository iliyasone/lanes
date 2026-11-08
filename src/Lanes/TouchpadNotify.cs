using System.Runtime.InteropServices;

namespace Lanes;

static class TouchpadNotify
{
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr SendMessageTimeout(IntPtr hwnd, int msg, IntPtr wp, string lp, uint flags, uint timeout, out IntPtr result);

    public static void Changed()
    {
        // WM_SETTINGCHANGE to every top-level window, as Settings does for most changes.
        SendMessageTimeout((IntPtr)0xFFFF, 0x001A, IntPtr.Zero, "Policy", 0x0002, 1000, out _);
    }
}
