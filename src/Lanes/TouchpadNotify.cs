using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Lanes;

// Explorer keeps its own copy of the touchpad gesture settings; writing the registry is not
// enough. The Settings app tells Explorer to reload them through IImmersiveSettingsCache
// (undocumented, stable since Windows 10 1511), and so does Lanes. The setting numbers change
// between Windows builds, so every number in a small range is announced: each just makes
// Explorer re-read that one setting from the registry.
static class TouchpadNotify
{
    public static void Changed()
    {
        if (Notify(out string why)) { Log.Write("Explorer told to reload touchpad settings"); return; }
        Log.Write($"telling Explorer directly failed ({why})");
        // An elevated process may not reach Explorer's COM objects; a copy of Lanes started
        // without elevation by the "Lanes notify" task can.
        if (Install.RunNotifyTask()) Log.Write("asked the unelevated helper to tell Explorer");
    }

    public static bool Notify(out string why)
    {
        why = "";
        try
        {
            var shell = (IServiceProvider)Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("c2f03a33-21f5-47fa-b4bb-156362a2f239"))!)!;
            var service = new Guid("53660488-8855-460b-a9ab-5cfc6b5012ca");
            var iid = new Guid("4214f6fa-eb36-4e2f-9ca2-23fdc1832df7");
            int hr = shell.QueryService(ref service, ref iid, out IntPtr p);
            if (hr != 0 || p == IntPtr.Zero) { why = $"QueryService 0x{hr:X8}"; return false; }
            try
            {
                var cache = (IImmersiveSettingsCache)Marshal.GetObjectForIUnknown(p);
                for (int id = 0; id < 0x80; id++) cache.OnSettingChanged(id);
            }
            finally
            {
                Marshal.Release(p);
                Marshal.FinalReleaseComObject(shell);
            }
            return true;
        }
        catch (Exception e)
        {
            why = e.Message;
            return false;
        }
    }

    [ComImport, Guid("4214f6fa-eb36-4e2f-9ca2-23fdc1832df7"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IImmersiveSettingsCache { void OnSettingChanged(int id); }

    [ComImport, Guid("6d5140c1-7436-11ce-8034-00aa006009fa"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IServiceProvider { [PreserveSig] int QueryService(ref Guid service, ref Guid riid, out IntPtr ppv); }
}
