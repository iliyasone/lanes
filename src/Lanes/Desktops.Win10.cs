using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace Lanes;

static partial class Desktops
{
    // Windows 10 (1809 to 22H2) has the same undocumented interfaces under other IDs and
    // layouts, which VirtualDesktopAccessor's Windows 11 build does not know; Lanes calls them
    // itself there (IDs as in Ciantic/VirtualDesktopAccessor's 2019-windows10 release and
    // MScholtes/VirtualDesktop). Each thread keeps its own connection to Explorer, made again
    // after Explorer restarts. Windows 10 cannot reorder its desktops, and renames them only
    // from 2004 on.
    static class Win10
    {
        // Windows 10 ends at build 19045; Server 2022 (20348) already has Windows 11's first layout.
        public static readonly bool On = Environment.OSVersion.Version.Build < 20000;

        sealed class Shell
        {
            public readonly IVirtualDesktopManagerInternal Manager;
            public readonly IVirtualDesktopManagerInternal2? Manager2;
            public readonly IVirtualDesktopManager Public;
            public readonly IApplicationViewCollection Views;
            public readonly IVirtualDesktopPinnedApps Pinned;
            public readonly IVirtualDesktopNotificationService Notifications;

            public Shell()
            {
                var shell = (IServiceProvider)Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("C2F03A33-21F5-47FA-B4BB-156362A2F239"))!)!;
                Manager = (IVirtualDesktopManagerInternal)Query(shell, new Guid("C5E0CDCA-7B6E-41B2-9FC4-D93975CC467B"), typeof(IVirtualDesktopManagerInternal).GUID);
                try { Manager2 = (IVirtualDesktopManagerInternal2)Query(shell, new Guid("C5E0CDCA-7B6E-41B2-9FC4-D93975CC467B"), typeof(IVirtualDesktopManagerInternal2).GUID); }
                catch (Exception) { Manager2 = null; } // before 2004
                Views = (IApplicationViewCollection)Query(shell, typeof(IApplicationViewCollection).GUID, typeof(IApplicationViewCollection).GUID);
                Pinned = (IVirtualDesktopPinnedApps)Query(shell, new Guid("B5A399E7-1C87-46B8-88E9-FC5747B171BD"), typeof(IVirtualDesktopPinnedApps).GUID);
                Notifications = (IVirtualDesktopNotificationService)Query(shell, new Guid("A501FDEC-4A09-464C-AE4E-1B9C21B84918"), typeof(IVirtualDesktopNotificationService).GUID);
                Public = (IVirtualDesktopManager)Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("AA509086-5CA9-4C25-8F95-589D3C07B48A"))!)!;
            }

            static object Query(IServiceProvider shell, Guid service, Guid iid)
            {
                Marshal.ThrowExceptionForHR(shell.QueryService(ref service, ref iid, out var p));
                try { return Marshal.GetObjectForIUnknown(p); }
                finally { Marshal.Release(p); }
            }
        }

        [ThreadStatic] static Shell? _shell;
        // The thread that listens for switches; Explorer forgets the listener when it restarts,
        // so that thread registers it again on every new connection.
        static IntPtr _listener;
        static uint _listenMessage;
        static int _listenThread = -1;

        // Runs f against Explorer, connecting again once if Explorer went away; `failed` on failure.
        static T Call<T>(Func<Shell, T> f, T failed)
        {
            for (int attempt = 0; attempt < 2; attempt++)
            {
                try
                {
                    if (_shell == null)
                    {
                        _shell = new Shell();
                        if (Environment.CurrentManagedThreadId == _listenThread) Register(_shell);
                    }
                    return f(_shell);
                }
                catch (COMException e) when (attempt == 0) { Log.Write($"virtual desktops (Windows 10): {e.Message}; connecting again"); _shell = null; }
                catch (Exception e) { Log.Write($"virtual desktops (Windows 10): {e.Message}"); _shell = null; return failed; }
            }
            return failed;
        }

        // Whether a call succeeded; Explorer gone (restarted) throws, so that Call connects again.
        static bool Ok(int hr)
        {
            if (hr is unchecked((int)0x80010108) or unchecked((int)0x800706BA) or unchecked((int)0x800706BE)) throw new COMException("Explorer is gone", hr);
            return hr >= 0;
        }

        static List<IVirtualDesktop> DesktopList(Shell s)
        {
            int hr = s.Manager.GetDesktops(out var array);
            if (!Ok(hr)) Marshal.ThrowExceptionForHR(hr);
            array.GetCount(out int n);
            var list = new List<IVirtualDesktop>(n);
            var iid = typeof(IVirtualDesktop).GUID;
            for (int i = 0; i < n; i++)
            {
                array.GetAt(i, ref iid, out var d);
                list.Add((IVirtualDesktop)d);
            }
            return list;
        }

        static Guid IdOf(IVirtualDesktop d) => Ok(d.GetId(out var id)) ? id : Guid.Empty;

        static IVirtualDesktop? At(Shell s, int number)
        {
            var list = DesktopList(s);
            return number >= 0 && number < list.Count ? list[number] : null;
        }

        static int WithView(Shell s, IntPtr hwnd, Func<IntPtr, int> f)
        {
            if (!Ok(s.Views.GetViewForHwnd(hwnd, out var view)) || view == IntPtr.Zero) return -1;
            try { return f(view); }
            finally { Marshal.Release(view); }
        }

        public static List<Guid> Ids() => Call(s => DesktopList(s).Select(IdOf).ToList(), new List<Guid>());

        public static int Count() => Call(s => Ok(s.Manager.GetCount(out int n)) ? n : -1, -1);

        public static int Current() => Call(s =>
        {
            if (!Ok(s.Manager.GetCurrentDesktop(out var current))) return -1;
            return DesktopList(s).Select(IdOf).ToList().IndexOf(IdOf(current));
        }, -1);

        public static Guid WindowDesktop(IntPtr hwnd) => Call(s => Ok(s.Public.GetWindowDesktopId(hwnd, out var id)) ? id : Guid.Empty, Guid.Empty);

        public static int OnCurrent(IntPtr hwnd) => Call(s => Ok(s.Public.IsWindowOnCurrentVirtualDesktop(hwnd, out int on)) ? (on != 0 ? 1 : 0) : -1, -1);

        public static int MoveWindow(IntPtr hwnd, int number) => Call(s =>
            At(s, number) is { } d ? WithView(s, hwnd, v => Ok(s.Manager.MoveViewToDesktop(v, d)) ? 1 : -1) : -1, -1);

        public static int GoTo(int number) => Call(s => At(s, number) is { } d && Ok(s.Manager.SwitchDesktop(d)) ? 1 : -1, -1);

        public static int Pin(IntPtr hwnd)
        {
            int r = Call(s => WithView(s, hwnd, v => Ok(s.Pinned.PinView(v)) ? 1 : -1), -1);
            if (r < 0) Log.Write($"virtual desktops (Windows 10): could not show window {hwnd:X} on every desktop");
            return r;
        }

        public static int IsPinned(IntPtr hwnd) => Call(s => WithView(s, hwnd, v => Ok(s.Pinned.IsViewPinned(v, out int on)) ? (on != 0 ? 1 : 0) : -1), -1);

        public static int Create() => Call(s => Ok(s.Manager.CreateDesktop(out _)) ? 1 : -1, -1);

        public static int Remove(int number, int fallback) => Call(s =>
            At(s, number) is { } d && At(s, fallback) is { } f && Ok(s.Manager.RemoveDesktop(d, f)) ? 1 : -1, -1);

        // Windows 10 keeps a desktop's name only in the registry.
        public static string Name(Guid id) =>
            Registry.GetValue($@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Explorer\VirtualDesktops\Desktops\{id:B}", "Name", null) as string ?? "";

        public static void Rename(int number, string name) => Call(s =>
        {
            if (s.Manager2 == null || At(s, number) is not { } d) return -1;
            Marshal.ThrowExceptionForHR(WindowsCreateString(name, name.Length, out var h));
            try { return Ok(s.Manager2.SetName(d, h)) ? 1 : -1; }
            finally { WindowsDeleteString(h); }
        }, -1);

        // Called on the thread that keeps listening (Lanes' engine thread).
        public static void Listen(IntPtr hwnd, uint message)
        {
            _listenMessage = message;
            _listener = hwnd;
            _listenThread = Environment.CurrentManagedThreadId;
            // A new connection registers the listener (see Call).
            _shell = null;
            Call(_ => 0, 0);
        }

        static void Register(Shell s)
        {
            int hr = s.Notifications.Register(new Listener(), out _);
            if (hr != 0) Log.Write($"virtual desktops (Windows 10): no switch notifications (0x{hr:X8})");
        }

        // Posts the message on every switch; Lanes reads where it is itself.
        sealed class Listener : IVirtualDesktopNotification
        {
            public int VirtualDesktopCreated(IntPtr d) => 0;
            public int VirtualDesktopDestroyBegin(IntPtr d, IntPtr fallback) => 0;
            public int VirtualDesktopDestroyFailed(IntPtr d, IntPtr fallback) => 0;
            public int VirtualDesktopDestroyed(IntPtr d, IntPtr fallback) => 0;
            public int ViewVirtualDesktopChanged(IntPtr view) => 0;
            public int CurrentVirtualDesktopChanged(IntPtr from, IntPtr to)
            {
                PostMessage(_listener, _listenMessage, IntPtr.Zero, IntPtr.Zero);
                return 0;
            }
        }

        [DllImport("user32.dll")] static extern bool PostMessage(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);
        [DllImport("combase.dll", CharSet = CharSet.Unicode)] static extern int WindowsCreateString(string s, int length, out IntPtr hstring);
        [DllImport("combase.dll")] static extern int WindowsDeleteString(IntPtr hstring);

        // Views (IApplicationView) are passed as raw pointers: Lanes only hands them back to Explorer.

        [ComImport, Guid("6D5140C1-7436-11CE-8034-00AA006009FA"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IServiceProvider { [PreserveSig] int QueryService(ref Guid service, ref Guid riid, out IntPtr ppv); }

        [ComImport, Guid("92CA9DCD-5622-4BBA-A805-5E9F541BD8C9"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IObjectArray
        {
            void GetCount(out int count);
            void GetAt(int index, ref Guid iid, [MarshalAs(UnmanagedType.Interface)] out object obj);
        }

        [ComImport, Guid("1841C6D7-4F9D-42C0-AF41-8747538F10E5"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IApplicationViewCollection
        {
            [PreserveSig] int GetViews(out IntPtr array);
            [PreserveSig] int GetViewsByZOrder(out IntPtr array);
            [PreserveSig] int GetViewsByAppUserModelId([MarshalAs(UnmanagedType.LPWStr)] string id, out IntPtr array);
            [PreserveSig] int GetViewForHwnd(IntPtr hwnd, out IntPtr view);
        }

        [ComImport, Guid("FF72FFDD-BE7E-43FC-9C03-AD81681E88E4"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IVirtualDesktop
        {
            [PreserveSig] int IsViewVisible(IntPtr view, out int visible);
            [PreserveSig] int GetId(out Guid id);
        }

        [ComImport, Guid("F31574D6-B682-4CDC-BD56-1827860ABEC6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IVirtualDesktopManagerInternal
        {
            [PreserveSig] int GetCount(out int count);
            [PreserveSig] int MoveViewToDesktop(IntPtr view, IVirtualDesktop desktop);
            [PreserveSig] int CanViewMoveDesktops(IntPtr view, out int can);
            [PreserveSig] int GetCurrentDesktop(out IVirtualDesktop desktop);
            [PreserveSig] int GetDesktops(out IObjectArray desktops);
            [PreserveSig] int GetAdjacentDesktop(IVirtualDesktop from, int direction, out IVirtualDesktop desktop);
            [PreserveSig] int SwitchDesktop(IVirtualDesktop desktop);
            [PreserveSig] int CreateDesktop(out IVirtualDesktop desktop);
            [PreserveSig] int RemoveDesktop(IVirtualDesktop desktop, IVirtualDesktop fallback);
            [PreserveSig] int FindDesktop(ref Guid id, out IVirtualDesktop desktop);
        }

        // From Windows 10 2004: the same with names.
        [ComImport, Guid("0F3A72B0-4566-487E-9A33-4ED302F6D6CE"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IVirtualDesktopManagerInternal2
        {
            [PreserveSig] int GetCount(out int count);
            [PreserveSig] int MoveViewToDesktop(IntPtr view, IVirtualDesktop desktop);
            [PreserveSig] int CanViewMoveDesktops(IntPtr view, out int can);
            [PreserveSig] int GetCurrentDesktop(out IVirtualDesktop desktop);
            [PreserveSig] int GetDesktops(out IObjectArray desktops);
            [PreserveSig] int GetAdjacentDesktop(IVirtualDesktop from, int direction, out IVirtualDesktop desktop);
            [PreserveSig] int SwitchDesktop(IVirtualDesktop desktop);
            [PreserveSig] int CreateDesktop(out IVirtualDesktop desktop);
            [PreserveSig] int RemoveDesktop(IVirtualDesktop desktop, IVirtualDesktop fallback);
            [PreserveSig] int FindDesktop(ref Guid id, out IVirtualDesktop desktop);
            [PreserveSig] int Unknown1(IVirtualDesktop desktop, out IntPtr a, out IntPtr b);
            [PreserveSig] int SetName(IVirtualDesktop desktop, IntPtr name);
        }

        // The documented one.
        [ComImport, Guid("A5CD92FF-29BE-454C-8D04-D82879FB3F1B"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IVirtualDesktopManager
        {
            [PreserveSig] int IsWindowOnCurrentVirtualDesktop(IntPtr hwnd, out int on);
            [PreserveSig] int GetWindowDesktopId(IntPtr hwnd, out Guid id);
        }

        [ComImport, Guid("4CE81583-1E4C-4632-A621-07A53543148F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IVirtualDesktopPinnedApps
        {
            [PreserveSig] int IsAppIdPinned([MarshalAs(UnmanagedType.LPWStr)] string appId, out int pinned);
            [PreserveSig] int PinAppID([MarshalAs(UnmanagedType.LPWStr)] string appId);
            [PreserveSig] int UnpinAppID([MarshalAs(UnmanagedType.LPWStr)] string appId);
            [PreserveSig] int IsViewPinned(IntPtr view, out int pinned);
            [PreserveSig] int PinView(IntPtr view);
            [PreserveSig] int UnpinView(IntPtr view);
        }

        [ComImport, Guid("C179334C-4295-40D3-BEA1-C654D965605A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IVirtualDesktopNotification
        {
            [PreserveSig] int VirtualDesktopCreated(IntPtr desktop);
            [PreserveSig] int VirtualDesktopDestroyBegin(IntPtr desktop, IntPtr fallback);
            [PreserveSig] int VirtualDesktopDestroyFailed(IntPtr desktop, IntPtr fallback);
            [PreserveSig] int VirtualDesktopDestroyed(IntPtr desktop, IntPtr fallback);
            [PreserveSig] int ViewVirtualDesktopChanged(IntPtr view);
            [PreserveSig] int CurrentVirtualDesktopChanged(IntPtr from, IntPtr to);
        }

        [ComImport, Guid("0CD45E71-D927-4F15-8B0A-8FEF525337BF"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IVirtualDesktopNotificationService
        {
            [PreserveSig] int Register(IVirtualDesktopNotification notification, out uint cookie);
            [PreserveSig] int Unregister(uint cookie);
        }
    }
}
