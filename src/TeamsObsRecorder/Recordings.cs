namespace TeamsObsRecorder;

public sealed class RecordingsConfig
{
    /// <summary>Rename and move recordings this app starts once they stop.</summary>
    public bool Organize { get; set; } = true;

    /// <summary>Where recordings go. Empty = the folder OBS saved them in.</summary>
    public string Folder { get; set; } = "";

    /// <summary>One subfolder per meeting name, so recurring meetings end up together.</summary>
    public bool GroupByMeeting { get; set; } = true;

    /// <summary>File name without extension. Tokens: {meeting} {date} {time}.</summary>
    public string FileName { get; set; } = "{date} {time} {meeting}";
}

public static class MeetingName
{
    private static readonly HashSet<string> TeamsSections = new(StringComparer.OrdinalIgnoreCase)
    {
        "Microsoft Teams", "Chat", "Activity", "Calendar", "Calls", "Teams", "Files", "Apps",
        "Copilot", "OneDrive", "Assignments", "Meet", "Communities", "Search", "Settings",
    };

    /// <summary>
    /// Best guess at the meeting's name. Meetings: the meeting window title. 1:1 and group calls:
    /// the call window, whose title is "Person | Microsoft Teams" (the main Teams window starts
    /// with a section such as "Chat | …", so it is skipped).
    /// </summary>
    public static string Guess(Detection detection, AppConfig config)
    {
        foreach (var w in detection.MeetingWindows)
        {
            var name = Clean(w.Title);
            if (name.Length > 0) return name;
        }

        var procs = config.ProcessNames.Select(MeetingMatcher.NormalizeProcess).ToHashSet();
        foreach (var w in detection.Windows)
        {
            var isTeams = w.Title.Contains("Microsoft Teams", StringComparison.OrdinalIgnoreCase) ||
                          (w.ProcessName.Length > 0 && procs.Contains(MeetingMatcher.NormalizeProcess(w.ProcessName)));
            if (!isTeams) continue;
            var parts = Parts(w.Title);
            if (parts.Count == 0 || TeamsSections.Contains(parts[0])) continue;
            var name = Clean(w.Title);
            if (name.Length > 0) return name;
        }
        return "Teams call";
    }

    private static List<string> Parts(string title) =>
        title.Split('|', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).ToList();

    /// <summary>"Meeting with Sara | Microsoft Teams" → "Meeting with Sara".</summary>
    public static string Clean(string title)
    {
        var parts = Parts(title).Where(p => !TeamsSections.Contains(p)).ToList();
        var name = parts.Count > 0 ? parts[0] : "";
        foreach (var suffix in new[] { " (External)", " (Guest)" })
            if (name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) name = name[..^suffix.Length];
        return name.Trim();
    }
}

public static class RecordingOrganizer
{
    private static readonly char[] Invalid =
        Path.GetInvalidFileNameChars().Concat(new[] { '<', '>', ':', '"', '/', '\\', '|', '?', '*' }).Distinct().ToArray();

    /// <summary>Makes a name safe for Windows and Linux file names.</summary>
    public static string Safe(string name, string fallback = "Teams call")
    {
        var chars = name.Select(c => Invalid.Contains(c) || char.IsControl(c) ? ' ' : c).ToArray();
        var safe = string.Join(' ', new string(chars).Split(' ', StringSplitOptions.RemoveEmptyEntries)).Trim(' ', '.');
        if (safe.Length > 80) safe = safe[..80].TrimEnd(' ', '.');
        return safe.Length > 0 ? safe : fallback;
    }

    public static string FileStem(RecordingsConfig cfg, string meeting, DateTime started)
    {
        var template = string.IsNullOrWhiteSpace(cfg.FileName) ? "{date} {time} {meeting}" : cfg.FileName;
        var stem = template
            .Replace("{meeting}", meeting, StringComparison.OrdinalIgnoreCase)
            .Replace("{date}", started.ToString("yyyy-MM-dd"), StringComparison.OrdinalIgnoreCase)
            .Replace("{time}", started.ToString("HH-mm"), StringComparison.OrdinalIgnoreCase);
        return Safe(stem, started.ToString("yyyy-MM-dd HH-mm"));
    }

    /// <summary>Where a recording of this meeting goes (without creating anything).</summary>
    public static string TargetFolder(RecordingsConfig cfg, string meeting, string obsFolder)
    {
        var root = string.IsNullOrWhiteSpace(cfg.Folder) ? obsFolder : Environment.ExpandEnvironmentVariables(cfg.Folder.Trim());
        return cfg.GroupByMeeting ? Path.Combine(root, Safe(meeting)) : root;
    }

    /// <summary>
    /// Moves a finished recording to its folder and name. Files with the same name and another
    /// extension (e.g. the .mp4 from OBS's automatic remux) are moved along with it.
    /// Returns the new path of the recording.
    /// </summary>
    public static string Organize(string recordingPath, string meeting, DateTime started, RecordingsConfig cfg,
        TimeSpan? settleDelay = null)
    {
        if (!cfg.Organize) return recordingPath;
        Thread.Sleep(settleDelay ?? TimeSpan.FromSeconds(5)); // let OBS finish writing / remuxing

        var sourceDir = Path.GetDirectoryName(recordingPath)!;
        var folder = TargetFolder(cfg, meeting, sourceDir);
        Directory.CreateDirectory(folder);
        var stem = FileStem(cfg, Safe(meeting), started);

        var originalStem = Path.GetFileNameWithoutExtension(recordingPath);
        var files = Directory.GetFiles(sourceDir, originalStem + ".*")
            .Where(f => Path.GetFileNameWithoutExtension(f) == originalStem).ToList();
        if (!files.Contains(recordingPath) && File.Exists(recordingPath)) files.Add(recordingPath);

        var unique = UniqueStem(folder, stem, files.Select(f => Path.GetExtension(f) ?? "").ToList());
        string result = recordingPath;
        foreach (var file in files)
        {
            var target = Path.Combine(folder, unique + Path.GetExtension(file));
            MoveWithRetry(file, target);
            Log.Info($"Recording saved as {target}");
            if (file == recordingPath) result = target;
        }
        return result;
    }

    private static string UniqueStem(string folder, string stem, List<string> extensions)
    {
        var candidate = stem;
        for (var n = 2; extensions.Any(e => File.Exists(Path.Combine(folder, candidate + e))); n++)
            candidate = $"{stem} ({n})";
        return candidate;
    }

    private static void MoveWithRetry(string from, string to)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                File.Move(from, to);
                return;
            }
            catch (IOException) when (attempt < 10)
            {
                Thread.Sleep(1000); // OBS may still hold the file for a moment
            }
        }
    }
}
