using System.Text.Json;
using System.Text.Json.Serialization;

namespace TeamsObsRecorder;

public sealed class AppConfig
{
    public List<string> TitlePatterns { get; set; } = new() { "*meeting*" };
    public List<string> ExcludeTitlePatterns { get; set; } = new();
    public List<string> ProcessNames { get; set; } = new() { "ms-teams", "teams", "teams-for-linux" };
    public double PollIntervalSeconds { get; set; } = 2;
    public double MeetingEndGraceSeconds { get; set; } = 10;
    public double PromptTimeoutSeconds { get; set; } = 60;
    public bool StopRecordingWhenMeetingEnds { get; set; } = true;
    /// <summary>Also treat "Teams is using the microphone" as a call (catches 1:1 and group calls).</summary>
    public bool DetectMicrophoneUse { get; set; } = true;
    public List<string> MicrophoneApps { get; set; } = new() { "*teams*" };
    /// <summary>Only count microphone use as a call when Teams is also playing sound.</summary>
    public bool RequireSoundWithMicrophone { get; set; }
    public ObsConfig Obs { get; set; } = new();
    public RecordingsConfig Recordings { get; set; } = new();

    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public static string DefaultDirectory
    {
        get
        {
            if (OperatingSystem.IsWindows())
                return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TeamsOBSRecorder");
            var xdg = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
            var baseDir = string.IsNullOrEmpty(xdg)
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config")
                : xdg;
            return Path.Combine(baseDir, "teams-obs-recorder");
        }
    }

    /// <summary>Loads the config, writing a default one first if the file doesn't exist.</summary>
    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(this, JsonOptions));
        File.Move(tmp, path, overwrite: true);
    }

    public static AppConfig Load(string path)
    {
        if (!File.Exists(path))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            var defaults = new AppConfig();
            File.WriteAllText(path, JsonSerializer.Serialize(defaults, JsonOptions));
            Log.Info($"Created default config at {path}");
            return defaults;
        }
        return JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(path), JsonOptions) ?? new AppConfig();
    }
}

public sealed class ObsConfig
{
    public string Host { get; set; } = "localhost";
    public int Port { get; set; } = 4455;
    public string Password { get; set; } = "";
    public bool LaunchIfNotRunning { get; set; } = true;
    public string Executable { get; set; } = "";
    public double StartupWaitSeconds { get; set; } = 20;
}
