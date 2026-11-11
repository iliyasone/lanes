using System.Runtime.InteropServices;

namespace Lanes;

// While Lanes runs, Windows' own animation of windows minimizing and coming back is off (System
// Properties → Performance → "Animate windows when minimizing and maximizing"): Lanes draws it
// instead, into and out of the app's taskbar button (Engine.Flights). Windows has one
// setting for minimizing and maximizing, so maximizing is instant meanwhile. Quitting puts back
// what you had.
static class WindowAnimation
{
    static readonly string Saved = Path.Combine(Log.Dir, "window-animation.txt");

    // Whether Windows' animation is off now, so Lanes draws its own.
    public static bool Taken { get; private set; }

    public static void TakeOver()
    {
        try
        {
            // Keep the first copy: after a crash the setting is already Lanes' own.
            if (!File.Exists(Saved))
            {
                Directory.CreateDirectory(Log.Dir);
                File.WriteAllText(Saved, Get() ? "1" : "0");
            }
            Taken = Set(false);
            Log.Write($"Windows' minimize animation: {(Taken ? "off, Lanes draws it" : "NOT TURNED OFF")}");
        }
        catch (Exception e) { Log.Write($"window animation: {e.Message}"); }
    }

    public static void GiveBack()
    {
        Taken = false;
        try
        {
            if (!File.Exists(Saved)) return;
            Set(File.ReadAllText(Saved).Trim() == "1");
            File.Delete(Saved);
            Log.Write("Windows' minimize animation given back");
        }
        catch (Exception e) { Log.Write($"window animation: {e.Message}"); }
    }

    [StructLayout(LayoutKind.Sequential)] struct ANIMATIONINFO { public int Size, MinAnimate; }

    static bool Get()
    {
        var a = new ANIMATIONINFO { Size = 8 };
        return SystemParametersInfo(0x0048 /* SPI_GETANIMATION */, 8, ref a, 0) && a.MinAnimate != 0;
    }

    static bool Set(bool on)
    {
        var a = new ANIMATIONINFO { Size = 8, MinAnimate = on ? 1 : 0 };
        return SystemParametersInfo(0x0049 /* SPI_SETANIMATION */, 8, ref a, 3 /* SPIF_UPDATEINIFILE | SPIF_SENDCHANGE */) && Get() == on;
    }

    [DllImport("user32.dll")] static extern bool SystemParametersInfo(uint action, uint param, ref ANIMATIONINFO info, uint winIni);
}
