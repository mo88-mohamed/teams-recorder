namespace TeamsObsRecorder;

/// <summary>Registers the app to start at login: HKCU Run key on Windows, XDG autostart on Linux.</summary>
public static class Autostart
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "TeamsOBSRecorder";

    private static string LinuxDesktopFile => Path.Combine(
        Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") is { Length: > 0 } xdg
            ? xdg
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config"),
        "autostart", "teams-obs-recorder.desktop");

    public static bool IsInstalled()
    {
        if (OperatingSystem.IsWindows())
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(ValueName) is not null;
        }
        return File.Exists(LinuxDesktopFile);
    }

    public static string Install()
    {
        var exe = Environment.ProcessPath ?? throw new InvalidOperationException("Cannot find own executable path");
        if (OperatingSystem.IsWindows())
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(RunKey);
            key.SetValue(ValueName, $"\"{exe}\"");
            return $"Added to HKCU\\{RunKey}";
        }

        Directory.CreateDirectory(Path.GetDirectoryName(LinuxDesktopFile)!);
        File.WriteAllText(LinuxDesktopFile,
            "[Desktop Entry]\n" +
            "Type=Application\n" +
            "Name=Teams OBS Recorder\n" +
            "Comment=Offer to record Teams meetings with OBS\n" +
            $"Exec=\"{exe}\"\n" +
            "X-GNOME-Autostart-enabled=true\n" +
            "NoDisplay=true\n");
        return $"Created {LinuxDesktopFile}";
    }

    public static string Uninstall()
    {
        if (OperatingSystem.IsWindows())
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            key?.DeleteValue(ValueName, throwOnMissingValue: false);
            return "Removed from startup";
        }
        if (File.Exists(LinuxDesktopFile)) File.Delete(LinuxDesktopFile);
        return $"Removed {LinuxDesktopFile}";
    }
}
