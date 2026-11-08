using System.Diagnostics;
using System.Reflection;

namespace Lanes;

static class Program
{
    public static readonly string Version = Assembly.GetExecutingAssembly().GetName().Version is { } v ? $"{v.Major}.{v.Minor}.{v.Build}" : "0.0.0";
    static Mutex? _single;

    [STAThread]
    static int Main(string[] args)
    {
        if (args.Contains("--install")) return Install.Run();
        if (args.Contains("--uninstall")) return Install.Remove();
        bool demo = args.Contains("--demo");

        _single = new Mutex(true, @"Local\Lanes", out bool owned);
        // After an update or a restart the old copy is still quitting: wait for it.
        if (!owned && args.Contains("--after-update"))
            try { owned = _single.WaitOne(TimeSpan.FromSeconds(10)); }
            catch (AbandonedMutexException) { owned = true; }
        if (!owned) return 0;

        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Log.Write($"Lanes {Version} starting{(demo ? " (demo)" : "")}");
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Log.Write($"crash: {e.ExceptionObject}");

        var settings = Settings.Load();
        var engine = new Engine(settings, demo);
        engine.Start();
        if (settings.Gestures && engine.Ready && !demo) WindowsGestures.TakeOver();
        // Whatever ends Lanes, Windows gets its gestures back.
        AppDomain.CurrentDomain.ProcessExit += (_, _) => { if (!demo) WindowsGestures.GiveBack(); };

        var updater = new Updater();
        using var tray = new Tray(engine, settings, updater);
        if (!demo) updater.Start();
        if (demo)
        {
            string dir = args.SkipWhile(a => a != "--demo").Skip(1).FirstOrDefault() ?? "demo";
            _ = Demo.Run(engine, dir);
        }
        Application.Run();
        engine.Stop();
        if (!demo) WindowsGestures.GiveBack();
        Log.Write("Lanes exited");
        _single.ReleaseMutex();
        return 0;
    }

    public static void Restart()
    {
        Process.Start(new ProcessStartInfo(Environment.ProcessPath!, "--after-update") { UseShellExecute = false });
        Application.Exit();
    }
}
