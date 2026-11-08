namespace Lanes;

// %LOCALAPPDATA%\Lanes\log.txt: start-up, touchpads, gestures, switches and errors.
static class Log
{
    public static readonly string Dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Lanes");
    public static readonly string File = Path.Combine(Dir, "log.txt");
    static readonly object Sync = new();

    public static void Write(string line)
    {
        lock (Sync)
        {
            try
            {
                Directory.CreateDirectory(Dir);
                var info = new FileInfo(File);
                if (info.Exists && info.Length > 2_000_000)
                {
                    System.IO.File.Delete(File + ".old");
                    System.IO.File.Move(File, File + ".old");
                }
                System.IO.File.AppendAllText(File, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {line}{Environment.NewLine}");
            }
            catch { }
        }
    }
}
