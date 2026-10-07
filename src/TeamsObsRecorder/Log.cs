namespace TeamsObsRecorder;

/// <summary>Minimal logger: writes to a log file next to the config and to the console.</summary>
public static class Log
{
    private static readonly object Gate = new();
    private static string? _file;

    public static void Init(string file)
    {
        var dir = Path.GetDirectoryName(file)!;
        if (!Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
            Secrets.RestrictToUser(dir, directory: true);
        }
        if (!File.Exists(file)) File.WriteAllText(file, "");
        Secrets.RestrictToUser(file); // it contains meeting names
        _file = file;
    }

    public static void Info(string message) => Write("INFO", message);

    public static void Warn(string message) => Write("WARN", message);

    public static void Error(string message, Exception? ex = null) =>
        Write("ERROR", ex is null ? message : $"{message}: {ex.GetType().Name}: {ex.Message}");

    private static void Write(string level, string message)
    {
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {level} {message}";
        lock (Gate)
        {
            try { Console.Error.WriteLine(line); } catch { /* no console */ }
            if (_file is not null)
            {
                try { File.AppendAllText(_file, line + Environment.NewLine); } catch { /* best effort */ }
            }
        }
    }
}
