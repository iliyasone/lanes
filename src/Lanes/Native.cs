using System.Runtime.InteropServices;
using System.Text;

namespace Lanes;

static class Native
{
    public const int WM_INPUT = 0x00FF, WM_MOUSEMOVE = 0x0200, WM_LBUTTONDOWN = 0x0201, WM_LBUTTONUP = 0x0202,
        WM_RBUTTONUP = 0x0205, WM_MBUTTONUP = 0x0208, WM_SYSCOMMAND = 0x0112, WM_KEYDOWN = 0x0100, WM_MOUSEACTIVATE = 0x0021, WM_SETCURSOR = 0x0020,
        WM_NCHITTEST = 0x0084, WM_DISPLAYCHANGE = 0x007E, WM_DPICHANGED = 0x02E0, WM_USER = 0x0400;
    public const int MA_NOACTIVATE = 3, SC_CLOSE = 0xF060;
    public const uint RID_INPUT = 0x10000003, RIDI_PREPARSEDDATA = 0x20000005, RIDI_DEVICENAME = 0x20000007;
    public const uint RIDEV_INPUTSINK = 0x00000100, RIM_TYPEHID = 2;
    public const int HIDP_STATUS_SUCCESS = 0x00110000, HidP_Input = 0;

    public const int GWL_STYLE = -16, GWL_EXSTYLE = -20;
    public const uint WS_POPUP = 0x80000000, WS_VISIBLE = 0x10000000, WS_CHILD = 0x40000000, WS_DISABLED = 0x08000000;
    public const uint WS_EX_TOPMOST = 0x8, WS_EX_TOOLWINDOW = 0x80, WS_EX_NOACTIVATE = 0x08000000, WS_EX_APPWINDOW = 0x40000,
        WS_EX_NOREDIRECTIONBITMAP = 0x00200000, WS_EX_LAYERED = 0x80000, WS_EX_TRANSPARENT = 0x20;
    public const uint SWP_NOSIZE = 0x1, SWP_NOMOVE = 0x2, SWP_NOZORDER = 0x4, SWP_NOACTIVATE = 0x10, SWP_SHOWWINDOW = 0x40, SWP_HIDEWINDOW = 0x80;
    public static readonly IntPtr HWND_TOPMOST = new(-1);
    public const int SW_HIDE = 0, SW_SHOWNOACTIVATE = 4, SW_RESTORE = 9;
    public const uint WDA_NONE = 0, WDA_EXCLUDEFROMCAPTURE = 0x11;
    public const uint MONITOR_DEFAULTTOPRIMARY = 1, MONITOR_DEFAULTTONEAREST = 2;
    public const uint GA_ROOTOWNER = 3;
    public const int DWMWA_EXTENDED_FRAME_BOUNDS = 9, DWMWA_CLOAKED = 14, DWMWA_WINDOW_CORNER_PREFERENCE = 33,
        DWMWA_USE_IMMERSIVE_DARK_MODE = 20, DWMWA_BORDER_COLOR = 34;
    public const uint DWM_TNP_RECTDESTINATION = 0x1, DWM_TNP_RECTSOURCE = 0x2, DWM_TNP_OPACITY = 0x4, DWM_TNP_VISIBLE = 0x8,
        DWM_TNP_SOURCECLIENTAREAONLY = 0x10;
    public const int SRCCOPY = 0x00CC0020, CAPTUREBLT = 0x40000000;
    public const uint INPUT_KEYBOARD = 1, KEYEVENTF_KEYUP = 0x2;

    [StructLayout(LayoutKind.Sequential)] public struct RAWINPUTDEVICE { public ushort UsagePage, Usage; public uint Flags; public IntPtr Target; }
    [StructLayout(LayoutKind.Sequential)] public struct RAWINPUTHEADER { public uint Type, Size; public IntPtr Device, WParam; }
    [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left, Top, Right, Bottom;
        public int Width => Right - Left;
        public int Height => Bottom - Top;
        public RECT(int l, int t, int r, int b) { Left = l; Top = t; Right = r; Bottom = b; }
        public override string ToString() => $"{Left},{Top} {Width}x{Height}";
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct MONITORINFO { public int Size; public RECT Monitor, Work; public uint Flags; }
    [StructLayout(LayoutKind.Sequential)]
    public struct DWM_THUMBNAIL_PROPERTIES { public uint Flags; public RECT Destination, Source; public byte Opacity; public int Visible, SourceClientAreaOnly; }
    [StructLayout(LayoutKind.Sequential)]
    public struct BITMAPINFOHEADER
    {
        public int Size, Width, Height; public short Planes, BitCount; public int Compression, SizeImage, XPelsPerMeter, YPelsPerMeter, ClrUsed, ClrImportant;
    }
    [StructLayout(LayoutKind.Sequential)] public struct KEYBDINPUT { public ushort Vk, Scan; public uint Flags, Time; public IntPtr ExtraInfo; }
    [StructLayout(LayoutKind.Sequential)] public struct MOUSEINPUT { public int Dx, Dy; public uint MouseData, Flags, Time; public IntPtr ExtraInfo; }
    [StructLayout(LayoutKind.Explicit)] public struct InputUnion { [FieldOffset(0)] public MOUSEINPUT Mouse; [FieldOffset(0)] public KEYBDINPUT Keyboard; }
    [StructLayout(LayoutKind.Sequential)] public struct INPUT { public uint Type; public InputUnion U; }

    public delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr param);

    [DllImport("user32.dll", SetLastError = true)] public static extern bool RegisterRawInputDevices(RAWINPUTDEVICE[] devices, uint count, uint size);
    [DllImport("user32.dll")] public static extern uint GetRawInputData(IntPtr rawInput, uint command, IntPtr data, ref uint size, uint headerSize);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern uint GetRawInputDeviceInfo(IntPtr device, uint command, IntPtr data, ref uint size);

    [DllImport("hid.dll")] public static extern int HidP_GetCaps(IntPtr preparsed, [Out] byte[] caps);
    [DllImport("hid.dll")] public static extern int HidP_GetSpecificValueCaps(int reportType, ushort usagePage, ushort linkCollection, ushort usage, [Out] byte[] valueCaps, ref ushort valueCapsLength, IntPtr preparsed);
    [DllImport("hid.dll")] public static extern int HidP_GetUsageValue(int reportType, ushort usagePage, ushort linkCollection, ushort usage, out uint value, IntPtr preparsed, IntPtr report, uint reportLength);
    [DllImport("hid.dll")] public static extern int HidP_GetUsages(int reportType, ushort usagePage, ushort linkCollection, [Out] ushort[] usages, ref uint usageLength, IntPtr preparsed, IntPtr report, uint reportLength);

    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hwnd, int cmd);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr hwnd);
    [DllImport("user32.dll")] public static extern bool IsWindow(IntPtr hwnd);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumWindowsProc proc, IntPtr param);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowText(IntPtr hwnd, StringBuilder text, int max);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetClassName(IntPtr hwnd, StringBuilder name, int max);
    [DllImport("user32.dll")] public static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);
    [DllImport("user32.dll")] public static extern IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr value);
    [DllImport("user32.dll")] public static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);
    [DllImport("user32.dll")] public static extern IntPtr GetLastActivePopup(IntPtr hwnd);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [DllImport("user32.dll")] public static extern bool SetWindowDisplayAffinity(IntPtr hwnd, uint affinity);
    [DllImport("user32.dll")] public static extern bool GetWindowDisplayAffinity(IntPtr hwnd, out uint affinity);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int RegisterWindowMessage(string name);
    [DllImport("user32.dll")] public static extern IntPtr SendMessageTimeout(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, uint flags, uint timeout, out IntPtr result);
    [DllImport("user32.dll")] public static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);
    [DllImport("user32.dll")] public static extern IntPtr MonitorFromPoint(POINT pt, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFO info);
    [DllImport("user32.dll")] public static extern uint GetDpiForWindow(IntPtr hwnd);
    [DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT point);
    [DllImport("user32.dll")] public static extern IntPtr SetCapture(IntPtr hwnd);
    [DllImport("user32.dll")] public static extern bool ReleaseCapture();
    [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] public static extern IntPtr GetDC(IntPtr hwnd);
    [DllImport("user32.dll")] public static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);
    [DllImport("user32.dll")] public static extern bool AllowSetForegroundWindow(int pid);
    [DllImport("user32.dll", SetLastError = true)] public static extern uint SendInput(uint count, INPUT[] inputs, int size);
    [DllImport("user32.dll")] public static extern IntPtr GetShellWindow();

    [DllImport("gdi32.dll")] public static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] public static extern IntPtr CreateDIBSection(IntPtr dc, ref BITMAPINFOHEADER info, uint usage, out IntPtr bits, IntPtr section, uint offset);
    [DllImport("gdi32.dll")] public static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
    [DllImport("gdi32.dll")] public static extern bool DeleteObject(IntPtr obj);
    [DllImport("gdi32.dll")] public static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll")] public static extern bool BitBlt(IntPtr dest, int x, int y, int w, int h, IntPtr src, int sx, int sy, int rop);

    [DllImport("dwmapi.dll")] public static extern int DwmRegisterThumbnail(IntPtr dest, IntPtr src, out IntPtr thumb);
    [DllImport("dwmapi.dll")] public static extern int DwmUnregisterThumbnail(IntPtr thumb);
    [DllImport("dwmapi.dll")] public static extern int DwmUpdateThumbnailProperties(IntPtr thumb, ref DWM_THUMBNAIL_PROPERTIES props);
    [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(IntPtr hwnd, int attr, out RECT value, int size);
    [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(IntPtr hwnd, int attr, out int value, int size);
    [DllImport("dwmapi.dll")] public static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
    [DllImport("dwmapi.dll")] public static extern int DwmFlush();

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr GetModuleHandle(string? name);

    public static string WindowText(IntPtr hwnd)
    {
        var sb = new StringBuilder(256);
        GetWindowText(hwnd, sb, sb.Capacity);
        return sb.ToString();
    }

    public static string ClassName(IntPtr hwnd)
    {
        var sb = new StringBuilder(256);
        GetClassName(hwnd, sb, sb.Capacity);
        return sb.ToString();
    }

    public static bool Cloaked(IntPtr hwnd) => DwmGetWindowAttribute(hwnd, DWMWA_CLOAKED, out int c, 4) == 0 && c != 0;
    // Hidden by the shell, as windows on other virtual desktops are (DWM_CLOAKED_SHELL).
    public static bool CloakedByShell(IntPtr hwnd) => DwmGetWindowAttribute(hwnd, DWMWA_CLOAKED, out int c, 4) == 0 && c == 2;

    // The window's visible rectangle, without the invisible resize borders.
    public static RECT Frame(IntPtr hwnd) =>
        DwmGetWindowAttribute(hwnd, DWMWA_EXTENDED_FRAME_BOUNDS, out RECT r, Marshal.SizeOf<RECT>()) == 0 ? r : GetWindowRect(hwnd, out r) ? r : default;

    public static MONITORINFO MonitorOf(IntPtr monitor)
    {
        var mi = new MONITORINFO { Size = Marshal.SizeOf<MONITORINFO>() };
        GetMonitorInfo(monitor, ref mi);
        return mi;
    }

    // The monitor the pointer is on.
    public static MONITORINFO PointerMonitor()
    {
        GetCursorPos(out var pt);
        return MonitorOf(MonitorFromPoint(pt, MONITOR_DEFAULTTOPRIMARY));
    }

    public static MONITORINFO PrimaryMonitor() => MonitorOf(MonitorFromPoint(default, MONITOR_DEFAULTTOPRIMARY));
}
