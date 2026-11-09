using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Windows.Automation;
using Lanes.Core;
using static Lanes.Native;

namespace Lanes;

// An app as the taskbar groups its windows: the id Windows gives it (see AppIds) and its name.
sealed record AppRef(string Id, string Name);

// A click on an app's taskbar button opens the overview with just that app's windows, from
// every desktop, when it has two or more (Windows would show their small previews); as a Mac
// shows an app's windows. A click with Shift, Ctrl or Alt held, any other mouse button, and a
// click on an app with one window go to Windows as before, and so does a press that becomes a
// drag (Lanes hands it back to Windows where it was).
//
// Windows draws its taskbar itself. Which button is where, Lanes reads through UI Automation
// (each button's "Appid: ..." and place) whenever the pointer comes over the taskbar, on a
// thread of its own. The click is caught by a low-level mouse hook on another thread, which
// only looks that up, so the pointer never waits for it.
sealed class TaskbarButtons
{
    public sealed record Button(AppRef App, RECT Rect, int Windows);

    readonly Action<AppRef> _clicked;
    volatile Button[] _buttons = Array.Empty<Button>();
    volatile RECT[] _bars = Array.Empty<RECT>();
    long _readAt;
    int _reading;
    bool _failed;
    readonly AutoResetEvent _read = new(true);
    TaskCompletionSource? _readDone;
    (Button Button, POINT At)? _pressed;
    IntPtr _hook;
    HookProc? _proc;

    public TaskbarButtons(Action<AppRef> clicked)
    {
        _clicked = clicked;
        var reader = new Thread(ReadLoop) { IsBackground = true, Name = "taskbar buttons" };
        reader.SetApartmentState(ApartmentState.STA);
        reader.Start();
        new Thread(HookLoop) { IsBackground = true, Name = "taskbar clicks" }.Start();
    }

    // ---------- the click ----------

    // Marks a press Lanes hands back to Windows, so the hook lets it by.
    static readonly IntPtr Ours = (IntPtr)0x4C414E45;

    void HookLoop()
    {
        _proc = OnMouse;
        _hook = SetWindowsHookEx(14 /* WH_MOUSE_LL */, _proc, GetModuleHandle(null), 0);
        if (_hook == IntPtr.Zero) { Log.Write("taskbar clicks: no mouse hook"); return; }
        while (MessageLoop.GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
        {
            MessageLoop.TranslateMessage(ref msg);
            MessageLoop.DispatchMessage(ref msg);
        }
    }

    IntPtr OnMouse(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0)
        {
            var m = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
            if (m.ExtraInfo != Ours)
                switch ((int)wParam)
                {
                    case WM_MOUSEMOVE:
                        if (_pressed is { } held)
                        {
                            if (Math.Abs(m.Pt.X - held.At.X) + Math.Abs(m.Pt.Y - held.At.Y) > 6)
                            {
                                // A drag along the taskbar: the press goes to Windows after all.
                                _pressed = null;
                                ThreadPool.QueueUserWorkItem(_ => Press(held.At));
                            }
                        }
                        else Near(m.Pt);
                        break;
                    case WM_LBUTTONDOWN:
                        if (!Held() && At(m.Pt) is { Windows: >= 2 } b)
                        {
                            _pressed = (b, m.Pt);
                            HidePreviews();
                            return 1;
                        }
                        break;
                    case WM_LBUTTONUP:
                        if (_pressed is { } click)
                        {
                            _pressed = null;
                            _clicked(click.Button.App);
                            return 1;
                        }
                        break;
                }
        }
        return CallNextHookEx(_hook, code, wParam, lParam);
    }

    // Windows' previews of the button's windows, up since the pointer rested on it, go: Lanes'
    // map comes in their place (and Windows never sees the click that would close them). They
    // are a pop-up of the taskbar's own (a XAML one in Windows 11 24H2, a classic window before).
    static void HidePreviews()
    {
        if (FindWindowEx(IntPtr.Zero, IntPtr.Zero, "Shell_TrayWnd", null) is var tray && tray == IntPtr.Zero) return;
        GetWindowThreadProcessId(tray, out uint shell);
        foreach (var cls in new[] { "Xaml_WindowedPopupClass", "ThumbnailDeviceHelperWnd", "TaskListThumbnailWnd" })
            for (IntPtr h = IntPtr.Zero; (h = FindWindowEx(IntPtr.Zero, h, cls, null)) != IntPtr.Zero;)
                if (IsWindowVisible(h) && GetWindowThreadProcessId(h, out uint pid) != 0 && pid == shell) ShowWindowAsync(h, 0 /* SW_HIDE */);
    }

    static bool Held() => GetAsyncKeyState(0x10) < 0 || GetAsyncKeyState(0x11) < 0 || GetAsyncKeyState(0x12) < 0;

    Button? At(POINT pt)
    {
        foreach (var b in _buttons)
            if (Inside(b.Rect, pt)) return OverTaskbar(pt) ? b : null;
        return null;
    }

    static bool Inside(RECT r, POINT pt) => pt.X >= r.Left && pt.X < r.Right && pt.Y >= r.Top && pt.Y < r.Bottom;

    // Whether the taskbar itself is under the pointer: not a full-screen window lying over it,
    // nor the overview.
    static bool OverTaskbar(POINT pt) =>
        ClassName(GetAncestor(WindowFromPoint(pt), 2 /* GA_ROOT */)) is "Shell_TrayWnd" or "Shell_SecondaryTrayWnd";

    // The pointer came over the taskbar: its buttons are read again, unless they just were (and
    // once a minute anyway, in case the taskbar itself has moved).
    void Near(POINT pt)
    {
        long since = Environment.TickCount64 - Interlocked.Read(ref _readAt);
        if (_reading == 1 || since < 1500) return;
        if (since < 60_000 && (!_bars.Any(r => Inside(r, pt)) || !OverTaskbar(pt))) return;
        Interlocked.Exchange(ref _readAt, Environment.TickCount64);
        _read.Set();
    }

    // Hands a press back to Windows, where it was.
    static void Press(POINT at)
    {
        int x0 = GetSystemMetrics(76), y0 = GetSystemMetrics(77), w = GetSystemMetrics(78), h = GetSystemMetrics(79);
        var input = new INPUT
        {
            Type = 0, // INPUT_MOUSE
            U = new InputUnion
            {
                Mouse = new MOUSEINPUT
                {
                    Dx = (int)Math.Round((at.X - x0) * 65535.0 / Math.Max(1, w - 1)),
                    Dy = (int)Math.Round((at.Y - y0) * 65535.0 / Math.Max(1, h - 1)),
                    Flags = 0x8000 | 0x4000 | 0x0001 | 0x0002, // ABSOLUTE | VIRTUALDESK | MOVE | LEFTDOWN
                    ExtraInfo = Ours,
                },
            },
        };
        SendInput(1, new[] { input }, Marshal.SizeOf<INPUT>());
    }

    // ---------- the buttons ----------

    void ReadLoop()
    {
        bool first = true;
        while (true)
        {
            _read.WaitOne();
            Interlocked.Exchange(ref _reading, 1);
            long t0 = Environment.TickCount64;
            try
            {
                _buttons = Read();
                if (first) Log.Write($"taskbar: {_buttons.Length} buttons read in {Environment.TickCount64 - t0} ms; app ids {AppIds.Source}");
                first = false;
            }
            catch (Exception e)
            {
                if (!_failed) Log.Write($"taskbar buttons: {e.Message}");
                _failed = true;
            }
            Interlocked.Exchange(ref _readAt, Environment.TickCount64);
            Interlocked.Exchange(ref _reading, 0);
            Interlocked.Exchange(ref _readDone, null)?.TrySetResult();
        }
    }

    Button[] Read()
    {
        var found = new List<(string Id, string Name, RECT Rect)>();
        var bars = new List<RECT>();
        var peer = new PropertyCondition(AutomationElement.ClassNameProperty, "Taskbar.TaskListButtonAutomationPeer");
        foreach (var cls in new[] { "Shell_TrayWnd", "Shell_SecondaryTrayWnd" })
            for (IntPtr h = IntPtr.Zero; (h = FindWindowEx(IntPtr.Zero, h, cls, null)) != IntPtr.Zero;)
            {
                if (!IsWindowVisible(h)) continue;
                bars.Add(Frame(h));
                foreach (AutomationElement e in AutomationElement.FromHandle(h).FindAll(TreeScope.Descendants, peer))
                {
                    var c = e.Current;
                    var r = c.BoundingRectangle;
                    if (!c.AutomationId.StartsWith("Appid: ", StringComparison.Ordinal) || r.IsEmpty) continue;
                    found.Add((c.AutomationId[7..], c.Name, new RECT((int)r.Left, (int)r.Top, (int)r.Right, (int)r.Bottom)));
                }
            }
        _bars = bars.ToArray();
        // How many windows each app has on all desktops together.
        var windows = AppWindows.Everywhere();
        return found.Select(b =>
        {
            var app = new AppRef(b.Id, TaskbarNames.App(b.Name));
            return new Button(app, b.Rect, windows.Count(w => AppIds.Is(w, app)));
        }).ToArray();
    }

    // For the demo run: reads the taskbar now and gives the button of `window`'s app.
    public async Task<Button?> ButtonOf(IntPtr window)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _readDone = done;
        _read.Set();
        await Task.WhenAny(done.Task, Task.Delay(5000));
        return _buttons.FirstOrDefault(b => AppIds.Is(window, b.App));
    }

    [StructLayout(LayoutKind.Sequential)]
    struct MSLLHOOKSTRUCT { public POINT Pt; public uint MouseData, Flags, Time; public IntPtr ExtraInfo; }

    delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] static extern IntPtr SetWindowsHookEx(int id, HookProc proc, IntPtr module, uint thread);
    [DllImport("user32.dll")] static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] static extern short GetAsyncKeyState(int vk);
    [DllImport("user32.dll")] static extern IntPtr WindowFromPoint(POINT pt);
    [DllImport("user32.dll")] static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll")] static extern bool ShowWindowAsync(IntPtr hwnd, int cmd);
}

// The id Windows groups an app's windows by on the taskbar ("Chrome", "Telegram.TelegramDesktop",
// or the program's path), from the shell's own resolver, the one the taskbar uses (not in the
// Windows SDK; the same since Windows 8). Without it, an app's windows go by its name.
static class AppIds
{
    [ThreadStatic] static IApplicationResolver? _resolver;
    [ThreadStatic] static bool _tried;
    static readonly ConcurrentDictionary<IntPtr, (uint Pid, string Id)> Known = new();
    public static string Source = "not asked yet";

    public static string? Of(IntPtr hwnd)
    {
        GetWindowThreadProcessId(hwnd, out uint pid);
        if (Known.TryGetValue(hwnd, out var k) && k.Pid == pid) return k.Id;
        if (!_tried)
        {
            _tried = true;
            try
            {
                _resolver = (IApplicationResolver)Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("660B90C8-73A9-4B58-8CAE-355B7F55341B"))!)!;
                Source = "from the shell";
            }
            catch (Exception e) { Source = $"by name (no resolver: {e.Message})"; }
        }
        if (_resolver == null || _resolver.GetAppIDForWindow(hwnd, out var p, out _, out _, out _) != 0 || p == IntPtr.Zero) return null;
        string id = Marshal.PtrToStringUni(p) ?? "";
        Marshal.FreeCoTaskMem(p);
        Known[hwnd] = (pid, id);
        return id;
    }

    // Whether a window is one of the app's.
    public static bool Is(IntPtr hwnd, AppRef app) =>
        Of(hwnd) is { } id ? string.Equals(id, app.Id, StringComparison.OrdinalIgnoreCase) : TaskbarNames.Same(app.Name, AppWindows.AppName(hwnd));

    [ComImport, Guid("DE25675A-72DE-44B4-9373-05170450C140"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IApplicationResolver
    {
        [PreserveSig] int GetAppIDForShortcut(IntPtr item, out IntPtr appId);
        [PreserveSig] int GetAppIDForShortcutObject(IntPtr link, IntPtr item, out IntPtr appId);
        [PreserveSig] int GetAppIDForWindow(IntPtr hwnd, out IntPtr appId, out int pinningPrevented, out int explicitAppId, out int embeddedShortcutValid);
    }
}
