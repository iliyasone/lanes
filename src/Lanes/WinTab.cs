using System.Runtime.InteropServices;

namespace Lanes;

// Win+Tab opens Lanes' overview in place of Windows' Task View (and closes it again). A
// low-level keyboard hook on a thread of its own keeps the Tab from Windows; the Start menu,
// which a Win key let go with no other key pressed would open, is held off by a key press that
// means nothing (as PowerToys does). The same hook gives Alt+Tab its keys (see AltTab).
sealed class WinTab
{
    readonly Action _pressed;
    readonly Func<int, bool, bool> _altTab;
    IntPtr _hook;
    HookProc? _proc;
    bool _taken;

    public WinTab(Action pressed, Func<int, bool, bool> altTab)
    {
        _pressed = pressed;
        _altTab = altTab;
        new Thread(HookLoop) { IsBackground = true, Name = "win+tab" }.Start();
    }

    void HookLoop()
    {
        _proc = OnKey;
        _hook = SetWindowsHookEx(13 /* WH_KEYBOARD_LL */, _proc, Native.GetModuleHandle(null), 0);
        if (_hook == IntPtr.Zero) { Log.Write("win+tab: no keyboard hook"); return; }
        while (MessageLoop.GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
        {
            MessageLoop.TranslateMessage(ref msg);
            MessageLoop.DispatchMessage(ref msg);
        }
    }

    IntPtr OnKey(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0 && _altTab(Marshal.ReadInt32(lParam), (int)wParam is 0x0100 or 0x0104)) return 1;
        if (code >= 0 && Marshal.ReadInt32(lParam) == 0x09 /* VK_TAB */)
        {
            bool down = (int)wParam is 0x0100 or 0x0104;
            if (down && Held(0x5B, 0x5C) && !Held(0x10) && !Held(0x11) && !Held(0x12))
            {
                if (!_taken)
                {
                    _taken = true;
                    keybd_event(0xE8, 0, 0, IntPtr.Zero);
                    keybd_event(0xE8, 0, 2 /* KEYEVENTF_KEYUP */, IntPtr.Zero);
                    _pressed();
                }
                return 1;
            }
            if (!down && _taken)
            {
                _taken = false;
                return 1;
            }
        }
        return CallNextHookEx(_hook, code, wParam, lParam);
    }

    static bool Held(params int[] keys) => keys.Any(k => GetAsyncKeyState(k) < 0);

    delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] static extern IntPtr SetWindowsHookEx(int id, HookProc proc, IntPtr module, uint thread);
    [DllImport("user32.dll")] static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] static extern short GetAsyncKeyState(int vk);
    [DllImport("user32.dll")] static extern void keybd_event(byte vk, byte scan, uint flags, IntPtr extra);
}
