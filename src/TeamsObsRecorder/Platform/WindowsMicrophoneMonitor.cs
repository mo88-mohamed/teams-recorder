using System.Runtime.Versioning;
using Microsoft.Win32;

namespace TeamsObsRecorder.Platform;

/// <summary>
/// Apps using the microphone come from two sources, combined: active capture streams from the
/// Core Audio API (see <see cref="WindowsAudioSessions"/>), and the same registry data that drives
/// the Windows "microphone in use" indicator. An app is using it when LastUsedTimeStart is set
/// and LastUsedTimeStop is 0. Packaged apps (new Teams: MSTeams_8wekyb3d8bbwe) are direct
/// subkeys; desktop apps (classic Teams.exe) are under NonPackaged with '#' as path separator.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsMicrophoneMonitor : IMicrophoneMonitor
{
    private const string ConsentStore =
        @"Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\microphone";

    public IReadOnlyList<string> AppsUsingMicrophone()
    {
        var apps = new List<string>();
        try { apps.AddRange(WindowsAudioSessions.ActiveApps(capture: true)); }
        catch (Exception ex) { LogOnce("Could not read microphone streams", ex); }
        try { apps.AddRange(FromRegistry()); }
        catch (Exception ex) { LogOnce("Could not read microphone usage from the registry", ex); }
        return apps.Distinct().ToList();
    }

    public IReadOnlyList<string> AppsPlayingSound()
    {
        try { return WindowsAudioSessions.ActiveApps(capture: false); }
        catch (Exception ex)
        {
            LogOnce("Could not read playback streams", ex);
            return Array.Empty<string>();
        }
    }

    private readonly HashSet<string> _logged = new();

    private void LogOnce(string message, Exception ex)
    {
        lock (_logged)
        {
            if (_logged.Add(message)) Log.Error(message, ex);
        }
    }

    private static List<string> FromRegistry()
    {
        var apps = new List<string>();
        using var root = Registry.CurrentUser.OpenSubKey(ConsentStore);
        if (root is null) return apps;

        foreach (var name in root.GetSubKeyNames())
        {
            using var key = root.OpenSubKey(name);
            if (key is null) continue;
            if (name.Equals("NonPackaged", StringComparison.OrdinalIgnoreCase))
            {
                foreach (var exe in key.GetSubKeyNames())
                {
                    using var exeKey = key.OpenSubKey(exe);
                    if (exeKey is not null && InUse(exeKey))
                        apps.Add(Path.GetFileName(exe.Replace('#', '\\')));
                }
            }
            else if (InUse(key))
            {
                apps.Add(name);
            }
        }
        return apps;
    }

    private static bool InUse(RegistryKey key) =>
        key.GetValue("LastUsedTimeStart") is long start && start > 0 &&
        key.GetValue("LastUsedTimeStop") is long stop && stop == 0;
}
