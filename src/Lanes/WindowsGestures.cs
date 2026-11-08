using Microsoft.Win32;

namespace Lanes;

// While Lanes runs, Windows' own four-finger swipes and three-finger up/down are set to
// "Nothing" so the two never react to the same swipe; quitting Lanes puts back what you had.
// Three-finger left/right and taps stay with Windows.
static class WindowsGestures
{
    const string Key = @"Software\Microsoft\Windows\CurrentVersion\PrecisionTouchPad";
    static readonly string[] Values = { "FourFingerLeft", "FourFingerRight", "FourFingerUp", "FourFingerDown", "ThreeFingerUp", "ThreeFingerDown" };
    static readonly string Saved = Path.Combine(Log.Dir, "windows-gestures.txt");

    // Whether this process holds the swipes now; only then does quitting give them back.
    public static bool Held { get; private set; }

    public static void TakeOver()
    {
        try
        {
            Held = true;
            using var key = Registry.CurrentUser.CreateSubKey(Key);
            // Keep the first copy: after a crash the values in the registry are already Lanes' own.
            if (!File.Exists(Saved))
            {
                Directory.CreateDirectory(Log.Dir);
                File.WriteAllLines(Saved, Values.Select(v => $"{v}={key.GetValue(v) ?? ""}")
                    .Append($"FourFingerSlideEnabled={key.GetValue("FourFingerSlideEnabled") ?? ""}")
                    .Append($"ThreeFingerSlideEnabled={key.GetValue("ThreeFingerSlideEnabled") ?? ""}"));
            }
            // 65535 is the "custom" choice that makes Windows read the per-direction values.
            key.SetValue("FourFingerSlideEnabled", 65535, RegistryValueKind.DWord);
            key.SetValue("ThreeFingerSlideEnabled", 65535, RegistryValueKind.DWord);
            foreach (var v in Values) key.SetValue(v, 0, RegistryValueKind.DWord);
            Apply();
            Log.Write("Windows gestures: four-finger swipes and three-finger up/down handed to Lanes");
        }
        catch (Exception e) { Log.Write($"take over gestures: {e.Message}"); }
    }

    public static void GiveBack(bool force = false)
    {
        if (!Held && !force) return;
        Held = false;
        try
        {
            if (!File.Exists(Saved)) return;
            using var key = Registry.CurrentUser.CreateSubKey(Key);
            foreach (var line in File.ReadAllLines(Saved))
            {
                var (name, value) = (line[..line.IndexOf('=')], line[(line.IndexOf('=') + 1)..]);
                if (value.Length == 0) key.DeleteValue(name, false);
                else key.SetValue(name, int.Parse(value), RegistryValueKind.DWord);
            }
            Apply();
            File.Delete(Saved);
            Log.Write("Windows gestures given back");
        }
        catch (Exception e) { Log.Write($"give back gestures: {e.Message}"); }
    }

    // Tells Windows the touchpad settings changed.
    static void Apply() => TouchpadNotify.Changed();
}
