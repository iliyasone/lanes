using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace Lanes;

// Looks for a newer release on GitHub at start and every four hours; the tray then offers
// "Update to X", which downloads Lanes.exe, swaps it in and restarts.
sealed class Updater
{
    const string Api = "https://api.github.com/repos/iliyasone/lanes/releases/latest";
    readonly SynchronizationContext? _ui = SynchronizationContext.Current;
    string? _url;

    public string? Available { get; private set; }
    public event Action? Found;

    record Asset([property: JsonPropertyName("name")] string Name, [property: JsonPropertyName("browser_download_url")] string Url);
    record Release([property: JsonPropertyName("tag_name")] string Tag, [property: JsonPropertyName("assets")] List<Asset> Assets);

    static HttpClient Client()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd($"Lanes/{Program.Version}");
        return c;
    }

    public void Start()
    {
        Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromSeconds(30));
            while (true)
            {
                await Check();
                await Task.Delay(TimeSpan.FromHours(4));
            }
        });
    }

    public async Task Check()
    {
        try
        {
            using var c = Client();
            var r = await c.GetFromJsonAsync<Release>(Api);
            if (r == null || !Version.TryParse(r.Tag.TrimStart('v'), out var v) || v <= Version.Parse(Program.Version)) return;
            var exe = r.Assets.FirstOrDefault(a => a.Name == "Lanes.exe");
            if (exe == null) return;
            _url = exe.Url;
            if (Available == v.ToString()) return;
            Available = v.ToString();
            Log.Write($"update available: {Available}");
            if (_ui != null) _ui.Post(_ => Found?.Invoke(), null); else Found?.Invoke();
        }
        catch (Exception e) { Log.Write($"update check: {e.Message}"); }
    }

    public void Install()
    {
        if (_url == null) return;
        Task.Run(async () =>
        {
            try
            {
                string dir = Path.Combine(Log.Dir, "update"), fresh = Path.Combine(dir, "Lanes.exe");
                Directory.CreateDirectory(dir);
                using (var c = Client())
                {
                    var bytes = await c.GetByteArrayAsync(_url);
                    if (bytes.Length < 100_000 || bytes[0] != 'M' || bytes[1] != 'Z') throw new InvalidDataException("not a program");
                    await File.WriteAllBytesAsync(fresh, bytes);
                }
                string exe = Environment.ProcessPath!, old = exe + ".old";
                try { File.Delete(old); } catch { }
                // A running exe cannot be overwritten, but it can be renamed.
                File.Move(exe, old);
                File.Move(fresh, exe);
                Log.Write($"updated to {Available}, restarting");
                Process.Start(new ProcessStartInfo(exe, "--after-update") { UseShellExecute = false });
                _ui?.Post(_ => Application.Exit(), null);
            }
            catch (Exception e) { Log.Write($"update: {e.Message}"); }
        });
    }
}
