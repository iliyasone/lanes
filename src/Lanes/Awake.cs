using System.Runtime.InteropServices;
using static Lanes.Native;

namespace Lanes;

// Chrome, Edge and the apps built on them (T3 Code, VS Code, Discord) stop drawing a window that
// is on another virtual desktop and catch up only when you arrive: a swipe or the overview shows
// it as it was, and some redraw with a jolt. They all draw on while Windows' Task View is open,
// so that its pictures are live, and learn of it from a window of Task View's class being shown
// (Windows 11's Task View has another class, so this one is free). Lanes keeps such a window
// shown, empty and off the screen, while it runs: every window stays live on every desktop, at
// the cost of the drawing they would have saved. Shown again now and then for a browser started
// since, or after the real Task View closed; hidden when Lanes quits, so they go back to normal.
sealed class Awake : IDisposable
{
    const string TaskViewClass = "MultitaskingViewFrame";
    readonly IntPtr _window;

    public Awake()
    {
        var wc = new WNDCLASSEX
        {
            Size = Marshal.SizeOf<WNDCLASSEX>(),
            WndProc = GetProcAddress(GetModuleHandle("user32.dll"), "DefWindowProcW"),
            Instance = GetModuleHandle(null),
            ClassName = TaskViewClass,
        };
        RegisterClassEx(ref wc);
        // WS_POPUP; tool window, never active, layered with nothing drawn, clicks pass through.
        _window = CreateWindowEx(0x80 | 0x08000000 | 0x80000 | 0x20, TaskViewClass, "", unchecked((int)0x80000000),
            -32000, -32000, 1, 1, IntPtr.Zero, IntPtr.Zero, wc.Instance, IntPtr.Zero);
        ShowWindow(_window, SW_SHOWNOACTIVATE);
    }

    public void Renew()
    {
        ShowWindow(_window, SW_HIDE);
        ShowWindow(_window, SW_SHOWNOACTIVATE);
    }

    public void Dispose()
    {
        ShowWindow(_window, SW_HIDE);
        DestroyWindow(_window);
    }

    const int SW_HIDE = 0, SW_SHOWNOACTIVATE = 4;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct WNDCLASSEX
    {
        public int Size, Style;
        public IntPtr WndProc;
        public int ClassExtra, WindowExtra;
        public IntPtr Instance, Icon, Cursor, Background;
        public string? MenuName, ClassName;
        public IntPtr SmallIcon;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern ushort RegisterClassEx(ref WNDCLASSEX wc);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr CreateWindowEx(int exStyle, string cls, string name, int style, int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);
    [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr hwnd, int cmd);
    [DllImport("user32.dll")] static extern bool DestroyWindow(IntPtr hwnd);
    [DllImport("kernel32.dll", CharSet = CharSet.Ansi)] static extern IntPtr GetProcAddress(IntPtr module, string name);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern IntPtr GetModuleHandle(string? name);
}
