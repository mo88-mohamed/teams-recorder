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
    /// <summary>Show a small always-on-top "REC" bar with the time and Pause/Stop while recording.</summary>
    public bool ShowRecordingIndicator { get; set; } = true;
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
        CreateDirectory(path);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, "");
        Secrets.RestrictToUser(tmp); // before the password is written
        File.WriteAllText(tmp, JsonSerializer.Serialize(this, JsonOptions));
        File.Move(tmp, path, overwrite: true);
    }

    private static void CreateDirectory(string path)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(path))!;
        if (Directory.Exists(dir)) return;
        Directory.CreateDirectory(dir);
        Secrets.RestrictToUser(dir, directory: true);
    }

    public static AppConfig Load(string path)
    {
        if (!File.Exists(path))
        {
            var defaults = new AppConfig();
            defaults.Save(path);
            Log.Info($"Created default config at {path}");
            return defaults;
        }
        var config = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(path), JsonOptions) ?? new AppConfig();
        Secrets.RestrictToUser(path);
        if (config.Obs.HasPlainPassword && Secrets.CanProtect)
        {
            // Typed in by hand or saved by an older version: replace it with the encrypted form.
            try
            {
                config.Save(path);
                Log.Info("Encrypted the OBS password in the config file");
            }
            catch (Exception ex)
            {
                Log.Error("Could not encrypt the OBS password in the config file", ex);
            }
        }
        return config;
    }
}

public sealed class ObsConfig
{
    public string Host { get; set; } = "localhost";
    public int Port { get; set; } = 4455;

    /// <summary>The password in memory. In the file it is "password_protected" (Windows) or "password".</summary>
    [JsonIgnore]
    public string Password { get; set; } = "";

    /// <summary>Plain-text password: read for hand-edited and older configs, written only where it can't be encrypted.</summary>
    [JsonPropertyName("password")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? PasswordPlain
    {
        get => Secrets.CanProtect ? null : Password;
        set
        {
            if (string.IsNullOrEmpty(value)) return;
            Password = value;
            HasPlainPassword = true;
        }
    }

    /// <summary>The password encrypted for this Windows user (DPAPI).</summary>
    [JsonPropertyName("password_protected")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? PasswordProtected
    {
        get => Secrets.CanProtect && Password.Length > 0 ? Secrets.Protect(Password) : null;
        set
        {
            if (string.IsNullOrEmpty(value) || HasPlainPassword) return; // a plain one typed in by hand wins
            Password = Secrets.Unprotect(value) ?? "";
        }
    }

    internal bool HasPlainPassword { get; private set; }

    public bool LaunchIfNotRunning { get; set; } = true;
    public string Executable { get; set; } = "";
    public double StartupWaitSeconds { get; set; } = 20;
}
