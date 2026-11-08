using System.Text.Json;
using Lanes.Core;

namespace Lanes;

// %LOCALAPPDATA%\Lanes\settings.json and lanes.json.
sealed class Settings
{
    public bool Gestures { get; set; } = true;
    // The picture moves with the fingers (as in Windows' own swipe); off reverses up and down.
    public bool Natural { get; set; } = true;

    static readonly string SettingsFile = Path.Combine(Log.Dir, "settings.json");
    static readonly string LanesFile = Path.Combine(Log.Dir, "lanes.json");
    static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static Settings Load()
    {
        try { return JsonSerializer.Deserialize<Settings>(File.ReadAllText(SettingsFile)) ?? new Settings(); }
        catch { return new Settings(); }
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Log.Dir);
            File.WriteAllText(SettingsFile, JsonSerializer.Serialize(this, Json));
        }
        catch (Exception e) { Log.Write($"settings: {e.Message}"); }
    }

    public static LaneMap? LoadLanes()
    {
        try { return File.Exists(LanesFile) ? LaneMap.Load(File.ReadAllText(LanesFile)) : null; }
        catch (Exception e)
        {
            Log.Write($"lanes.json unreadable, starting over: {e.Message}");
            return null;
        }
    }

    public static void SaveLanes(LaneMap map)
    {
        try
        {
            Directory.CreateDirectory(Log.Dir);
            File.WriteAllText(LanesFile, map.Save());
        }
        catch (Exception e) { Log.Write($"lanes.json: {e.Message}"); }
    }
}
