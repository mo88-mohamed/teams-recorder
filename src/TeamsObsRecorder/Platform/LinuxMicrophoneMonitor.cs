using System.Text.RegularExpressions;

namespace TeamsObsRecorder.Platform;

/// <summary>
/// Lists apps recording from a microphone via `pactl list source-outputs`
/// (PulseAudio, or PipeWire with pipewire-pulse); apps playing sound via `pactl list sink-inputs`.
/// </summary>
public sealed class LinuxMicrophoneMonitor : IMicrophoneMonitor
{
    private bool _warned;

    public IReadOnlyList<string> AppsUsingMicrophone()
    {
        if (Shell.Which("pactl") is null)
        {
            if (!_warned) Log.Info("pactl not found; only window titles are used to detect calls");
            _warned = true;
            return Array.Empty<string>();
        }
        var (code, output) = Shell.Run("pactl", new[] { "list", "source-outputs" }, TimeSpan.FromSeconds(5));
        return code == 0 ? Parse(output) : Array.Empty<string>();
    }

    public IReadOnlyList<string> AppsPlayingSound()
    {
        if (Shell.Which("pactl") is null) return Array.Empty<string>();
        var (code, output) = Shell.Run("pactl", new[] { "list", "sink-inputs" }, TimeSpan.FromSeconds(5));
        return code == 0 ? Parse(output, "Sink Input #") : Array.Empty<string>();
    }

    private static readonly Regex Property =
        new(@"^\s*application\.(process\.binary|name)\s*=\s*""(.*)""\s*$", RegexOptions.Multiline);

    /// <summary>One entry per recording stream: its process binary, or its app name if that's missing.</summary>
    public static List<string> Parse(string pactlOutput, string header = "Source Output #")
    {
        var apps = new List<string>();
        foreach (var block in Regex.Split(pactlOutput, "^" + Regex.Escape(header), RegexOptions.Multiline).Skip(1))
        {
            var props = new Dictionary<string, string>();
            foreach (Match m in Property.Matches(block)) props.TryAdd(m.Groups[1].Value, m.Groups[2].Value);
            if (props.TryGetValue("process.binary", out var binary)) apps.Add(binary);
            else if (props.TryGetValue("name", out var name)) apps.Add(name);
        }
        return apps;
    }
}
