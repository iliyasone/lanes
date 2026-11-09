using System.Runtime.InteropServices;

namespace Lanes;

// Tells when the volume of the default speakers changes, so Explorer's pop-up window can come
// back at once while Lanes keeps Windows' pill away (see WindowsPill): Windows shows the volume
// in that window. Watches the device that is the default when Watch is called.
static class VolumeWatch
{
    public static event Action? Changed;

    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")] class MMDeviceEnumerator { }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int dataFlow, int stateMask, out IntPtr devices);
        [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice endpoint);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid iid, int clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object iface);
    }

    [ComImport, Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioEndpointVolume
    {
        [PreserveSig] int RegisterControlChangeNotify(IAudioEndpointVolumeCallback callback);
        [PreserveSig] int UnregisterControlChangeNotify(IAudioEndpointVolumeCallback callback);
    }

    [ComImport, Guid("657804FA-D6AD-4496-8A60-352752AF4F89"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioEndpointVolumeCallback
    {
        [PreserveSig] int OnNotify(IntPtr data);
    }

    sealed class Callback : IAudioEndpointVolumeCallback
    {
        public int OnNotify(IntPtr data)
        {
            Changed?.Invoke();
            return 0;
        }
    }

    static readonly Callback Listener = new();
    static IAudioEndpointVolume? _watched;
    static readonly object Lock = new();

    // On the thread pool: COM calls to the audio service can take a moment.
    public static void Watch() => Task.Run(() =>
    {
        lock (Lock)
        {
            try
            {
                var devices = (IMMDeviceEnumerator)new MMDeviceEnumerator();
                if (devices.GetDefaultAudioEndpoint(0 /* eRender */, 1 /* eMultimedia */, out var speakers) != 0) return;
                var iid = typeof(IAudioEndpointVolume).GUID;
                if (speakers.Activate(ref iid, 1 /* CLSCTX_INPROC_SERVER */, IntPtr.Zero, out var o) != 0) return;
                _watched?.UnregisterControlChangeNotify(Listener);
                _watched = (IAudioEndpointVolume)o;
                _watched.RegisterControlChangeNotify(Listener);
            }
            catch (Exception e) { Log.Write($"volume watch: {e.Message}"); }
        }
    });
}
