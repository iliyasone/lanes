using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;

namespace Lanes;

// Windows' virtual desktops, through VirtualDesktopAccessor (the interfaces are undocumented and
// change between Windows builds; that DLL follows them). Desktops are named by their IDs, which
// Windows also keeps in the registry in their order.
static class Desktops
{
    const string Dll = "VirtualDesktopAccessor.dll";

    [DllImport(Dll)] static extern int GetCurrentDesktopNumber();
    [DllImport(Dll)] static extern int GetDesktopCount();
    [DllImport(Dll)] static extern Guid GetDesktopIdByNumber(int number);
    [DllImport(Dll)] static extern Guid GetWindowDesktopId(IntPtr hwnd);
    [DllImport(Dll)] static extern int IsWindowOnCurrentVirtualDesktop(IntPtr hwnd);
    [DllImport(Dll)] static extern int MoveWindowToDesktopNumber(IntPtr hwnd, int number);
    [DllImport(Dll)] static extern int GoToDesktopNumber(int number);
    [DllImport(Dll)] static extern int GetDesktopName(int number, byte[] utf8, nuint length);
    [DllImport(Dll)] static extern int SetDesktopName(int number, byte[] utf8);
    [DllImport(Dll)] static extern int RegisterPostMessageHook(IntPtr hwnd, uint messageOffset);
    [DllImport(Dll)] static extern int PinWindow(IntPtr hwnd);
    [DllImport(Dll)] static extern int IsPinnedWindow(IntPtr hwnd);
    [DllImport(Dll)] static extern int CreateDesktop();
    [DllImport(Dll)] static extern int RemoveDesktop(int number, int fallback);
    [DllImport(Dll)] static extern int MoveDesktop(int number, int newIndex);

    static IntPtr _library;

    // Writes the embedded DLL next to the log and loads it; false when this Windows is not supported.
    public static bool Load()
    {
        try
        {
            string path = Path.Combine(Log.Dir, Dll);
            using (var res = Assembly.GetExecutingAssembly().GetManifestResourceStream(Dll)!)
            {
                var bytes = new byte[res.Length];
                res.ReadExactly(bytes);
                if (!File.Exists(path) || !File.ReadAllBytes(path).AsSpan().SequenceEqual(bytes))
                {
                    Directory.CreateDirectory(Log.Dir);
                    File.WriteAllBytes(path, bytes);
                }
            }
            _library = NativeLibrary.Load(path);
            NativeLibrary.SetDllImportResolver(typeof(Desktops).Assembly, (name, _, _) => name == Dll ? _library : IntPtr.Zero);
            int count = GetDesktopCount();
            Log.Write($"virtual desktops: {count}, current {GetCurrentDesktopNumber()}");
            return count > 0;
        }
        catch (Exception e)
        {
            Log.Write($"virtual desktops unavailable: {e.Message}");
            return false;
        }
    }

    public static int Count => GetDesktopCount();

    // The desktops in Windows' order.
    public static List<Guid> All()
    {
        int n = GetDesktopCount();
        // -1 while Explorer restarts.
        var ids = new List<Guid>(Math.Max(0, n));
        for (int i = 0; i < n; i++) ids.Add(GetDesktopIdByNumber(i));
        if (ids.Count > 0 && ids.All(g => g != Guid.Empty) && ids.Distinct().Count() == ids.Count) return ids;
        return FromRegistry();
    }

    static List<Guid> FromRegistry()
    {
        var ids = new List<Guid>();
        if (Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Explorer\VirtualDesktops", "VirtualDesktopIDs", null) is byte[] raw)
            for (int i = 0; i + 16 <= raw.Length; i += 16) ids.Add(new Guid(raw.AsSpan(i, 16)));
        return ids;
    }

    public static Guid Current
    {
        get
        {
            int i = GetCurrentDesktopNumber();
            var all = All();
            return i >= 0 && i < all.Count ? all[i] : Guid.Empty;
        }
    }

    public static int IndexOf(Guid desktop) => All().IndexOf(desktop);

    // Switches at once, without Windows' own slide.
    public static bool Switch(Guid desktop)
    {
        int i = IndexOf(desktop);
        return i >= 0 && GoToDesktopNumber(i) >= 0;
    }

    // The name Windows shows for the desktop; empty goes back to "Desktop N".
    public static void Rename(Guid desktop, string name)
    {
        int i = IndexOf(desktop);
        if (i >= 0) SetDesktopName(i, Encoding.UTF8.GetBytes(name + "\0"));
    }

    public static Guid Create()
    {
        int before = GetDesktopCount();
        CreateDesktop();
        var all = All();
        return all.Count > before ? all[^1] : Guid.Empty;
    }

    public static void Remove(Guid desktop, Guid fallback)
    {
        int i = IndexOf(desktop), f = IndexOf(fallback);
        if (i >= 0 && f >= 0 && i != f) RemoveDesktop(i, f);
    }

    static bool _canMove = true;

    // Reorders Windows' own list; the 2024-12 build of the DLL has no MoveDesktop, so this is best effort.
    public static void Move(Guid desktop, int index)
    {
        if (!_canMove) return;
        int i = IndexOf(desktop);
        try { if (i >= 0 && i != index) MoveDesktop(i, index); }
        catch (EntryPointNotFoundException) { _canMove = false; }
    }

    public static string Name(Guid desktop)
    {
        int i = IndexOf(desktop);
        if (i < 0) return "";
        var buf = new byte[512];
        int n = GetDesktopName(i, buf, (nuint)buf.Length);
        if (n <= 0) return "";
        int len = Array.IndexOf(buf, (byte)0);
        return Encoding.UTF8.GetString(buf, 0, len < 0 ? buf.Length : len);
    }

    public static Guid WindowDesktop(IntPtr hwnd) => GetWindowDesktopId(hwnd);
    public static bool OnCurrent(IntPtr hwnd) => IsWindowOnCurrentVirtualDesktop(hwnd) == 1;
    public static void MoveWindow(IntPtr hwnd, Guid desktop) => MoveWindowToDesktopNumber(hwnd, IndexOf(desktop));
    public static void Pin(IntPtr hwnd) { if (IsPinnedWindow(hwnd) != 1) PinWindow(hwnd); }

    // Windows will post `message` to hwnd (wParam old number, lParam new number) on every switch.
    public static void Listen(IntPtr hwnd, uint message) => RegisterPostMessageHook(hwnd, message);
}
