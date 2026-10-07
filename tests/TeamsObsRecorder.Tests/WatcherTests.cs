using TeamsObsRecorder;
using TeamsObsRecorder.Platform;
using Xunit;

namespace TeamsObsRecorder.Tests;

public class WatcherTests
{
    private sealed class FakeLister : IWindowLister
    {
        public List<WindowInfo> Windows { get; } = new();
        public IReadOnlyList<WindowInfo> List() => Windows.ToList();
    }

    private sealed class FakePrompt : IPrompt
    {
        public PromptChoice Answer { get; set; } = PromptChoice.Record;
        public List<string> Messages { get; } = new();
        public PromptChoice Ask(string title, string message, TimeSpan timeout)
        {
            Messages.Add(message);
            return Answer;
        }
    }

    private sealed class FakeRecorder : IRecorder
    {
        public bool AlreadyRecording { get; set; }
        public List<string> Events { get; } = new();
        public bool StartRecording() { Events.Add("start"); return !AlreadyRecording; }
        public string? OutputPath { get; set; }
        public string? StopRecording() { Events.Add("stop"); return OutputPath; }
    }

    private sealed class FakeMicrophone : IMicrophoneMonitor
    {
        public List<string> Apps { get; } = new();
        public List<string> Playing { get; } = new();
        public IReadOnlyList<string> AppsUsingMicrophone() => Apps.ToList();
        public IReadOnlyList<string> AppsPlayingSound() => Playing.ToList();
    }

    private static readonly DateTime T0 = new(2026, 1, 1, 9, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void FindMeetings_MatchesOnlyTeamsMeetingWindows()
    {
        var windows = new[]
        {
            new WindowInfo("1", "Chat | Microsoft Teams", "ms-teams"),
            new WindowInfo("2", "Meeting notes.txt - Notepad", "notepad"),
            new WindowInfo("3", "Meeting with Sara | Microsoft Teams", "ms-teams"),
            new WindowInfo("4", "MEETING in General | Microsoft Teams", "teams-for-linux"),
            new WindowInfo("5", "Meeting with Ali | Microsoft Teams", ""), // process unknown
        };
        var config = new AppConfig { ProcessNames = new() { "ms-teams.exe", "teams-for-linux" } };

        var ids = MeetingMatcher.FindMeetings(windows, config).Select(w => w.Id);

        Assert.Equal(new[] { "3", "4", "5" }, ids);
    }

    [Fact]
    public void FindMeetings_EmptyProcessListAllowsAnyAppAndExcludesApply()
    {
        var windows = new[]
        {
            new WindowInfo("1", "Meeting with Sara | Microsoft Teams - Edge", "msedge"),
            new WindowInfo("2", "Meeting notes.txt - Notepad", "notepad"),
        };
        var config = new AppConfig { ProcessNames = new(), ExcludeTitlePatterns = new() { "*notes*" } };

        Assert.Equal(new[] { "1" }, MeetingMatcher.FindMeetings(windows, config).Select(w => w.Id));
    }

    [Fact]
    public async Task OnePromptPerMeeting_RecordsAndStopsAfterGrace()
    {
        var lister = new FakeLister();
        var prompt = new FakePrompt();
        var recorder = new FakeRecorder();
        var watcher = new MeetingWatcher(new AppConfig(), lister, prompt, recorder);

        lister.Windows.Add(new WindowInfo("1", "Chat | Microsoft Teams", "ms-teams"));
        watcher.Tick(T0);
        Assert.False(watcher.InMeeting);

        lister.Windows.Add(new WindowInfo("2", "Meeting with Sara | Microsoft Teams", "ms-teams"));
        watcher.Tick(T0.AddSeconds(2));
        await watcher.PromptTask!;
        Assert.Equal(new[] { "start" }, recorder.Events);

        // Teams opens a compact window and the title changes: still the same meeting.
        lister.Windows[1] = new WindowInfo("2", "Meeting in General | Microsoft Teams", "ms-teams");
        lister.Windows.Add(new WindowInfo("3", "Meeting compact view | Microsoft Teams", "ms-teams"));
        watcher.Tick(T0.AddSeconds(4));

        lister.Windows.RemoveRange(1, 2);
        watcher.Tick(T0.AddSeconds(6));
        Assert.True(watcher.InMeeting); // within the grace period
        watcher.Tick(T0.AddSeconds(20));
        Assert.False(watcher.InMeeting);

        Assert.Single(prompt.Messages);
        Assert.Contains("Meeting with Sara", prompt.Messages[0]);
        Assert.Equal(new[] { "start", "stop" }, recorder.Events);
    }

    [Fact]
    public async Task Dismiss_DoesNotRecord()
    {
        var lister = new FakeLister();
        var recorder = new FakeRecorder();
        var watcher = new MeetingWatcher(new AppConfig(), lister,
            new FakePrompt { Answer = PromptChoice.Dismiss }, recorder);

        lister.Windows.Add(new WindowInfo("2", "Meeting with Sara | Microsoft Teams", "ms-teams"));
        watcher.Tick(T0);
        await watcher.PromptTask!;
        lister.Windows.Clear();
        watcher.Tick(T0.AddSeconds(30));

        Assert.Empty(recorder.Events);
    }

    [Fact]
    public async Task RecordingStartedElsewhere_IsNotStopped()
    {
        var lister = new FakeLister();
        var recorder = new FakeRecorder { AlreadyRecording = true };
        var watcher = new MeetingWatcher(new AppConfig(), lister, new FakePrompt(), recorder);

        lister.Windows.Add(new WindowInfo("2", "Meeting with Sara | Microsoft Teams", "ms-teams"));
        watcher.Tick(T0);
        await watcher.PromptTask!;
        lister.Windows.Clear();
        watcher.Tick(T0.AddSeconds(30));

        Assert.Equal(new[] { "start" }, recorder.Events);
    }

    [Fact]
    public void ParsesWmctrlOutput()
    {
        const string output =
            "0x03a00007  0 1234 host Chat | Microsoft Teams\n" +
            "0x03a0000b  0 0    host Meeting with Sara | Microsoft Teams\n";

        var windows = LinuxWindowLister.Parse(output, pid => pid == 1234 ? "teams-for-linux" : "");

        Assert.Equal(new[]
        {
            new WindowInfo("0x03a00007", "Chat | Microsoft Teams", "teams-for-linux"),
            new WindowInfo("0x03a0000b", "Meeting with Sara | Microsoft Teams", ""),
        }, windows);
    }

    [Fact]
    public void Config_ReadsSnakeCaseAndWritesDefaults()
    {
        var dir = Directory.CreateTempSubdirectory().FullName;
        var path = Path.Combine(dir, "config.json");

        var created = AppConfig.Load(path);
        Assert.Contains("\"title_patterns\"", File.ReadAllText(path));
        Assert.Equal(4455, created.Obs.Port);

        File.WriteAllText(path, """
            { "title_patterns": ["*call*"], "obs": { "password": "secret", "port": 4460 } }
            """);
        var loaded = AppConfig.Load(path);
        Assert.Equal(new[] { "*call*" }, loaded.TitlePatterns);
        Assert.Equal("secret", loaded.Obs.Password);
        Assert.Equal(4460, loaded.Obs.Port);
        Assert.Equal("localhost", loaded.Obs.Host); // unspecified keys keep defaults
    }

    [Fact]
    public async Task OneToOneCall_DetectedByTeamsMicrophoneUse()
    {
        var lister = new FakeLister();
        var mic = new FakeMicrophone();
        var prompt = new FakePrompt();
        var recorder = new FakeRecorder();
        var watcher = new MeetingWatcher(new AppConfig(), lister, prompt, recorder, mic);

        // A 1:1 call window is titled with the other person's name, not "meeting".
        lister.Windows.Add(new WindowInfo("1", "Sara Ahmed | Microsoft Teams", "ms-teams"));
        mic.Apps.Add("Zoom.exe");
        watcher.Tick(T0);
        Assert.False(watcher.InMeeting); // other apps using the mic don't count

        mic.Apps.Add("MSTeams_8wekyb3d8bbwe");
        watcher.Tick(T0.AddSeconds(2));
        await watcher.PromptTask!;
        Assert.True(watcher.InMeeting);
        Assert.Contains("Call: Sara Ahmed", prompt.Messages.Single()); // named after the call window

        mic.Apps.Clear();
        watcher.Tick(T0.AddSeconds(30));
        Assert.False(watcher.InMeeting);
        Assert.Equal(new[] { "start", "stop" }, recorder.Events);
    }

    [Fact]
    public void MicrophoneDetection_CanBeTurnedOff()
    {
        var mic = new FakeMicrophone();
        mic.Apps.Add("ms-teams.exe");
        var watcher = new MeetingWatcher(new AppConfig { DetectMicrophoneUse = false },
            new FakeLister(), new FakePrompt(), new FakeRecorder(), mic);

        watcher.Tick(T0);

        Assert.False(watcher.InMeeting);
    }

    [Fact]
    public void ParsesPactlSourceOutputs()
    {
        const string output = """
            Source Output #57
            	Driver: protocol-native.c
            	Properties:
            		application.name = "teams-for-linux"
            		application.process.binary = "teams-for-linux"
            		media.name = "RecordStream"

            Source Output #58
            	Properties:
            		application.name = "Firefox"
            """;

        Assert.Equal(new[] { "teams-for-linux", "Firefox" }, LinuxMicrophoneMonitor.Parse(output.Replace("\\t", "\t")));
    }

    [Fact]
    public void RequireSound_NeedsTeamsOnMicAndSpeakers()
    {
        var mic = new FakeMicrophone();
        var config = new AppConfig { RequireSoundWithMicrophone = true };
        var watcher = new MeetingWatcher(config, new FakeLister(), new FakePrompt(), new FakeRecorder(), mic);

        mic.Apps.Add("ms-teams.exe");
        mic.Playing.Add("Spotify.exe");
        watcher.Tick(T0);
        Assert.False(watcher.InMeeting);

        // Teams often plays audio from a WebView2 helper; the parent chain still matches *teams*.
        mic.Playing.Add("msedgewebview2.exe < ms-teams.exe");
        watcher.Tick(T0.AddSeconds(2));
        Assert.True(watcher.InMeeting);
    }

    [Fact]
    public void ParsesPactlSinkInputs()
    {
        const string output = "Sink Input #12\n\tProperties:\n\t\tapplication.process.binary = \"teams-for-linux\"\n";
        Assert.Equal(new[] { "teams-for-linux" }, LinuxMicrophoneMonitor.Parse(output, "Sink Input #"));
    }

    [Fact]
    public async Task FinishedRecording_IsMovedIntoMeetingFolder()
    {
        var obsDir = Directory.CreateTempSubdirectory().FullName;
        var mkv = Path.Combine(obsDir, "obs-name.mkv");
        File.WriteAllText(mkv, "video");
        var lister = new FakeLister();
        var recorder = new FakeRecorder { OutputPath = mkv };
        var watcher = new MeetingWatcher(new AppConfig(), lister, new FakePrompt(), recorder) { OrganizeDelay = TimeSpan.Zero };

        lister.Windows.Add(new WindowInfo("2", "Meeting with Sara | Microsoft Teams", "ms-teams"));
        watcher.Tick(T0);
        await watcher.PromptTask!;
        lister.Windows.Clear();
        watcher.Tick(T0.AddSeconds(30));
        await watcher.OrganizeTask!;

        var folder = Path.Combine(obsDir, "Meeting with Sara");
        Assert.Single(Directory.GetFiles(folder, "* Meeting with Sara.mkv"));
        Assert.False(File.Exists(mkv));
    }
}
