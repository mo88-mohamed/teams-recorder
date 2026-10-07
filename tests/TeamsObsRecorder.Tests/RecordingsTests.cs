using TeamsObsRecorder;
using Xunit;

namespace TeamsObsRecorder.Tests;

public class RecordingsTests
{
    private static readonly DateTime Started = new(2026, 10, 7, 14, 30, 0);

    private sealed class Lister : IWindowLister
    {
        public List<WindowInfo> Windows { get; } = new();
        public IReadOnlyList<WindowInfo> List() => Windows.ToList();
    }

    private static Detection Detect(AppConfig config, params WindowInfo[] windows)
    {
        var lister = new Lister();
        lister.Windows.AddRange(windows);
        return Detection.Run(lister, null, config);
    }

    [Fact]
    public void MeetingName_FromMeetingWindow()
    {
        var d = Detect(new AppConfig(),
            new WindowInfo("1", "Chat | Sara Ahmed | Microsoft Teams", "ms-teams"),
            new WindowInfo("2", "Meeting in General | Weekly Sync | Microsoft Teams", "ms-teams"));

        Assert.Equal("Meeting in General", MeetingName.Guess(d, new AppConfig()));
    }

    [Fact]
    public void MeetingName_ForOneToOneCall_UsesCallWindowNotMainWindow()
    {
        var d = Detect(new AppConfig(),
            new WindowInfo("1", "Chat | Omar Ali | Microsoft Teams", "ms-teams"),
            new WindowInfo("2", "Sara Ahmed (External) | Microsoft Teams", "ms-teams"),
            new WindowInfo("3", "Report.docx - Word", "winword"));

        Assert.Equal("Sara Ahmed", MeetingName.Guess(d, new AppConfig()));
    }

    [Fact]
    public void MeetingName_FallsBackToTeamsCall()
    {
        var d = Detect(new AppConfig(), new WindowInfo("1", "Calendar | Calendar | Microsoft Teams", "ms-teams"));
        Assert.Equal("Teams call", MeetingName.Guess(d, new AppConfig()));
    }

    [Fact]
    public void FileName_UsesTemplateAndIsSafe()
    {
        var cfg = new RecordingsConfig { FileName = "{meeting} - {date} {time}" };
        Assert.Equal("Weekly Sync - 2026-10-07 14-30", RecordingOrganizer.FileStem(cfg, "Weekly Sync", Started));
        Assert.Equal("Q4 plan", RecordingOrganizer.Safe("Q4: plan?"));
        Assert.Equal("a b", RecordingOrganizer.Safe("a/b"));
        Assert.Equal("Teams call", RecordingOrganizer.Safe("///"));
    }

    [Fact]
    public void Organize_MovesRecordingAndRemuxIntoMeetingFolder()
    {
        var obsDir = Directory.CreateTempSubdirectory().FullName;
        var target = Directory.CreateTempSubdirectory().FullName;
        var mkv = Path.Combine(obsDir, "2026-10-07 14-30-11.mkv");
        File.WriteAllText(mkv, "video");
        File.WriteAllText(Path.ChangeExtension(mkv, ".mp4"), "remuxed");
        File.WriteAllText(Path.Combine(obsDir, "other.mkv"), "unrelated");
        var cfg = new RecordingsConfig { Folder = target };

        var saved = RecordingOrganizer.Organize(mkv, "Weekly Sync", Started, cfg, TimeSpan.Zero);

        var folder = Path.Combine(target, "Weekly Sync");
        Assert.Equal(Path.Combine(folder, "2026-10-07 14-30 Weekly Sync.mkv"), saved);
        Assert.True(File.Exists(Path.Combine(folder, "2026-10-07 14-30 Weekly Sync.mp4")));
        Assert.True(File.Exists(Path.Combine(obsDir, "other.mkv")));
        Assert.False(File.Exists(mkv));

        // A second recording with the same name gets " (2)" instead of overwriting.
        File.WriteAllText(mkv, "video 2");
        var second = RecordingOrganizer.Organize(mkv, "Weekly Sync", Started, cfg, TimeSpan.Zero);
        Assert.Equal(Path.Combine(folder, "2026-10-07 14-30 Weekly Sync (2).mkv"), second);
    }

    [Fact]
    public void Organize_Off_LeavesFileAlone()
    {
        var dir = Directory.CreateTempSubdirectory().FullName;
        var mkv = Path.Combine(dir, "x.mkv");
        File.WriteAllText(mkv, "video");

        var saved = RecordingOrganizer.Organize(mkv, "Weekly Sync", Started,
            new RecordingsConfig { Organize = false }, TimeSpan.Zero);

        Assert.Equal(mkv, saved);
        Assert.True(File.Exists(mkv));
    }

    [Fact]
    public void Organize_NoFolderSet_GroupsNextToObsRecordings()
    {
        var obsDir = Directory.CreateTempSubdirectory().FullName;
        var mkv = Path.Combine(obsDir, "obs.mkv");
        File.WriteAllText(mkv, "video");

        var saved = RecordingOrganizer.Organize(mkv, "Sara Ahmed", Started, new RecordingsConfig(), TimeSpan.Zero);

        Assert.Equal(Path.Combine(obsDir, "Sara Ahmed", "2026-10-07 14-30 Sara Ahmed.mkv"), saved);
    }
}
