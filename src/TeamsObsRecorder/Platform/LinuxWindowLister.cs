namespace TeamsObsRecorder.Platform;

/// <summary>Lists windows with wmctrl (X11 / XWayland).</summary>
public sealed class LinuxWindowLister : IWindowLister
{
    public IReadOnlyList<WindowInfo> List()
    {
        if (Shell.Which("wmctrl") is null)
            throw new InvalidOperationException("wmctrl not found; install it (e.g. sudo apt install wmctrl)");
        var (_, output) = Shell.Run("wmctrl", new[] { "-lp" }, TimeSpan.FromSeconds(5));
        return Parse(output);
    }

    /// <summary>`wmctrl -lp` columns: id desktop pid host title.</summary>
    public static List<WindowInfo> Parse(string wmctrlOutput, Func<int, string>? processName = null)
    {
        processName ??= ProcessName;
        var results = new List<WindowInfo>();
        foreach (var line in wmctrlOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.Split((char[]?)null, 5, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 5) continue;
            int.TryParse(parts[2], out var pid);
            results.Add(new WindowInfo(parts[0], parts[4].TrimEnd('\r'), processName(pid)));
        }
        return results;
    }

    private static string ProcessName(int pid)
    {
        if (pid <= 0) return "";
        try
        {
            var target = new FileInfo($"/proc/{pid}/exe").LinkTarget;
            if (!string.IsNullOrEmpty(target)) return Path.GetFileName(target);
        }
        catch { /* fall back to comm */ }
        try { return File.ReadAllText($"/proc/{pid}/comm").Trim(); }
        catch { return ""; }
    }
}
