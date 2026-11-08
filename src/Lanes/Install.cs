using System.Diagnostics;
using System.Security.Principal;

namespace Lanes;

// "Lanes.exe --install": copies itself to Program Files and starts with Windows through a
// Task Scheduler task that runs elevated (so swipes work over admin windows too) without a
// prompt. "--uninstall" stops it, gives the gestures back to Windows and removes it.
static class Install
{
    public const string TaskName = "Lanes";
    static readonly string Dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Lanes");
    public static string Exe => Path.Combine(Dir, "Lanes.exe");

    static bool Elevated => new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);

    static bool Relaunched(string arg)
    {
        if (Elevated) return false;
        try { Process.Start(new ProcessStartInfo(Environment.ProcessPath!, arg) { UseShellExecute = true, Verb = "runas" }); }
        catch { MessageBox.Show("Lanes needs administrator rights to install.", "Lanes"); }
        return true;
    }

    public static int Run()
    {
        if (Relaunched("--install")) return 0;
        StopRunning();
        Directory.CreateDirectory(Dir);
        if (!string.Equals(Environment.ProcessPath, Exe, StringComparison.OrdinalIgnoreCase))
        {
            try { File.Delete(Exe + ".old"); } catch { }
            if (File.Exists(Exe)) File.Move(Exe, Exe + ".old", true);
            File.Copy(Environment.ProcessPath!, Exe, true);
        }
        string user = WindowsIdentity.GetCurrent().Name;
        string xml = Path.Combine(Path.GetTempPath(), "lanes-task.xml");
        File.WriteAllText(xml, $"""
            <?xml version="1.0" encoding="UTF-16"?>
            <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <Triggers><LogonTrigger><Enabled>true</Enabled><UserId>{user}</UserId></LogonTrigger></Triggers>
              <Principals><Principal id="Author"><UserId>{user}</UserId><LogonType>InteractiveToken</LogonType><RunLevel>HighestAvailable</RunLevel></Principal></Principals>
              <Settings>
                <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
                <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
                <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
                <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
                <Priority>4</Priority>
              </Settings>
              <Actions Context="Author"><Exec><Command>{Exe}</Command></Exec></Actions>
            </Task>
            """, System.Text.Encoding.Unicode);
        Schtasks($"/Create /TN {TaskName} /XML \"{xml}\" /F");
        File.Delete(xml);
        Schtasks($"/Run /TN {TaskName}");
        Log.Write($"installed {Program.Version} to {Dir}");
        return 0;
    }

    public static int Remove()
    {
        if (Relaunched("--uninstall")) return 0;
        StopRunning();
        WindowsGestures.GiveBack();
        Schtasks($"/Delete /TN {TaskName} /F");
        // The folder may hold this very program; remove it once it has exited.
        Process.Start(new ProcessStartInfo("cmd.exe", $"/c timeout /t 2 /nobreak >nul & rmdir /s /q \"{Dir}\"") { CreateNoWindow = true, UseShellExecute = false });
        Log.Write("uninstalled");
        return 0;
    }

    static void StopRunning()
    {
        foreach (var p in Process.GetProcessesByName("Lanes"))
            if (p.Id != Environment.ProcessId)
                try { p.Kill(); p.WaitForExit(3000); } catch { }
    }

    static void Schtasks(string args)
    {
        using var p = Process.Start(new ProcessStartInfo("schtasks.exe", args) { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true })!;
        p.WaitForExit(15000);
        Log.Write($"schtasks {args.Split(' ')[0]}: {p.ExitCode} {p.StandardError.ReadToEnd().Trim()}");
    }
}
