using System.Text.RegularExpressions;

namespace TeamsObsRecorder;

public sealed record WindowInfo(string Id, string Title, string ProcessName);

public enum PromptChoice { Record, Dismiss }

public interface IWindowLister
{
    IReadOnlyList<WindowInfo> List();
}

public interface IPrompt
{
    PromptChoice Ask(string title, string message, TimeSpan timeout);
}

public interface IMicrophoneMonitor
{
    /// <summary>Names of the apps recording from a microphone right now.</summary>
    IReadOnlyList<string> AppsUsingMicrophone();

    /// <summary>Names of the apps playing sound right now.</summary>
    IReadOnlyList<string> AppsPlayingSound() => Array.Empty<string>();
}

public interface IRecorder
{
    /// <summary>Starts recording. Returns false if OBS was already recording.</summary>
    bool StartRecording();
    /// <summary>Stops recording; returns the saved file's path when known.</summary>
    string? StopRecording();
    void PauseRecording() { }
    void ResumeRecording() { }
    /// <summary>Whether OBS is recording right now; null when that can't be checked.</summary>
    bool? IsRecordingActive() => null;
}

public static class MeetingMatcher
{
    public static bool Glob(string text, string pattern)
    {
        var regex = "^" + Regex.Escape(pattern).Replace(@"\*", ".*").Replace(@"\?", ".") + "$";
        return Regex.IsMatch(text, regex, RegexOptions.IgnoreCase | RegexOptions.Singleline);
    }

    /// <summary>"ms-teams.exe" and "ms-teams" are treated the same.</summary>
    public static string NormalizeProcess(string name)
    {
        var n = name.Trim().ToLowerInvariant();
        return n.EndsWith(".exe") ? n[..^4] : n;
    }

    public static List<WindowInfo> FindMeetings(IEnumerable<WindowInfo> windows, AppConfig config)
    {
        var procs = config.ProcessNames.Select(NormalizeProcess).ToHashSet();
        return windows.Where(w =>
            config.TitlePatterns.Any(p => Glob(w.Title, p)) &&
            !config.ExcludeTitlePatterns.Any(p => Glob(w.Title, p)) &&
            // A window whose process can't be determined is allowed through.
            (procs.Count == 0 || w.ProcessName.Length == 0 || procs.Contains(NormalizeProcess(w.ProcessName))))
            .ToList();
    }

    public static List<string> FindMicrophoneApps(IEnumerable<string> apps, AppConfig config) =>
        config.DetectMicrophoneUse
            ? apps.Where(a => config.MicrophoneApps.Any(p => Glob(a, p))).Distinct().ToList()
            : new List<string>();
}

/// <summary>Everything the app can see right now, and whether it counts as a meeting or call.</summary>
public sealed class Detection
{
    public List<WindowInfo> Windows { get; private init; } = new();
    public List<WindowInfo> MeetingWindows { get; private init; } = new();
    public List<string> MicrophoneApps { get; private init; } = new();
    public List<string> MicrophoneMatches { get; private init; } = new();
    public List<string> SoundApps { get; private init; } = new();
    public List<string> SoundMatches { get; private init; } = new();
    public List<string> Errors { get; } = new();

    /// <summary>Teams uses the mic (and, if required, also plays sound).</summary>
    public bool CallByAudio { get; private init; }

    public bool InMeetingOrCall => MeetingWindows.Count > 0 || CallByAudio;

    public static Detection Run(IWindowLister lister, IMicrophoneMonitor? audio, AppConfig config)
    {
        var errors = new List<string>();
        var windows = new List<WindowInfo>();
        try { windows = lister.List().ToList(); }
        catch (Exception ex) { errors.Add($"Could not list windows: {ex.Message}"); }

        var mic = new List<string>();
        var sound = new List<string>();
        if (audio is not null && config.DetectMicrophoneUse)
        {
            try { mic = audio.AppsUsingMicrophone().ToList(); }
            catch (Exception ex) { errors.Add($"Could not check microphone use: {ex.Message}"); }
            try { sound = audio.AppsPlayingSound().ToList(); }
            catch (Exception ex) { errors.Add($"Could not check sound playback: {ex.Message}"); }
        }

        var micMatches = MeetingMatcher.FindMicrophoneApps(mic, config);
        var soundMatches = MeetingMatcher.FindMicrophoneApps(sound, config);
        var result = new Detection
        {
            Windows = windows,
            MeetingWindows = MeetingMatcher.FindMeetings(windows, config),
            MicrophoneApps = mic,
            MicrophoneMatches = micMatches,
            SoundApps = sound,
            SoundMatches = soundMatches,
            CallByAudio = micMatches.Count > 0 && (!config.RequireSoundWithMicrophone || soundMatches.Count > 0),
        };
        result.Errors.AddRange(errors);
        foreach (var e in errors) Log.Error(e);
        return result;
    }
}

/// <summary>
/// A "meeting session" starts when a matching window appears or Teams starts using the
/// microphone (1:1 and group calls have no "meeting" window title), and ends once neither has
/// been seen for MeetingEndGraceSeconds. Teams' compact/pop-out windows and title changes therefore
/// don't trigger extra prompts.
/// </summary>
public sealed class MeetingWatcher
{
    private volatile AppConfig _config;
    private readonly IWindowLister _lister;
    private readonly IPrompt _prompt;
    private readonly IRecorder _recorder;
    private readonly IMicrophoneMonitor? _microphone;
    private readonly object _gate = new();
    private DateTime _lastSeen;
    private bool _weStartedRecording;
    private string _meetingName = "";
    private DateTime _recordingStarted;
    private TimeSpan _elapsedBeforePause;
    private DateTime? _runningSinceUtc;
    private DateTime _lastObsCheck;

    /// <summary>For tests and the clock: current UTC time.</summary>
    public Func<DateTime> UtcNow { get; set; } = () => DateTime.UtcNow;

    public bool InMeeting { get; private set; }

    /// <summary>While paused, new meetings are ignored; a recording already started still stops at the end.</summary>
    public bool Paused { get; set; }

    /// <summary>Settings can be swapped while running (e.g. saved from the settings window).</summary>
    public AppConfig Config
    {
        get => _config;
        set => _config = value;
    }
    /// <summary>Name of the current meeting or call ("" when none).</summary>
    public string MeetingTitle
    {
        get { lock (_gate) return InMeeting ? _meetingName : ""; }
    }

    /// <summary>True while a recording this app started is running (or paused).</summary>
    public bool IsRecording
    {
        get { lock (_gate) return _weStartedRecording; }
    }

    public bool IsRecordingPaused
    {
        get { lock (_gate) return _weStartedRecording && _runningSinceUtc is null; }
    }

    /// <summary>How long the current recording has run, not counting pauses.</summary>
    public TimeSpan RecordingElapsed
    {
        get
        {
            lock (_gate)
            {
                if (!_weStartedRecording) return TimeSpan.Zero;
                var running = _runningSinceUtc is { } since ? UtcNow() - since : TimeSpan.Zero;
                return _elapsedBeforePause + (running > TimeSpan.Zero ? running : TimeSpan.Zero);
            }
        }
    }

    public Task? PromptTask { get; private set; }
    public Task? OrganizeTask { get; private set; }

    /// <summary>For tests: how long to wait before moving a finished recording.</summary>
    public TimeSpan? OrganizeDelay { get; set; }

    public MeetingWatcher(AppConfig config, IWindowLister lister, IPrompt prompt, IRecorder recorder,
        IMicrophoneMonitor? microphone = null)
    {
        _microphone = microphone;
        _config = config;
        _lister = lister;
        _prompt = prompt;
        _recorder = recorder;
    }

    public void Tick(DateTime now)
    {
        var detection = Detection.Run(_lister, _microphone, _config);
        var meetings = detection.MeetingWindows;
        var micApps = detection.CallByAudio ? detection.MicrophoneMatches : new List<string>();

        lock (_gate)
        {
            if (meetings.Count > 0 || micApps.Count > 0)
            {
                if (Paused && !InMeeting) return;
                _lastSeen = now;
                if (!InMeeting)
                {
                    InMeeting = true;
                    _meetingName = MeetingName.Guess(detection, _config);
                    var title = meetings.Count > 0 ? meetings[0].Title : $"Call: {_meetingName}";
                    Log.Info(meetings.Count > 0
                        ? $"Meeting detected: {title}"
                        : $"Call detected: microphone in use by {string.Join(", ", micApps)}");
                    PromptTask = Task.Run(() => AskAndRecord(title));
                }
                else if (_meetingName == "Teams call")
                {
                    _meetingName = MeetingName.Guess(detection, _config); // call window may appear later
                }
                CheckStoppedInObs(now);
                return;
            }

            if (InMeeting && (now - _lastSeen).TotalSeconds >= _config.MeetingEndGraceSeconds)
            {
                Log.Info("Meeting ended");
                InMeeting = false;
                if (_weStartedRecording && _config.StopRecordingWhenMeetingEnds) StopLocked();
                _weStartedRecording = false;
                _runningSinceUtc = null;
            }
        }
    }

    /// <summary>Notices when the recording was stopped in OBS itself, so the indicator goes away.</summary>
    private void CheckStoppedInObs(DateTime now)
    {
        if (!_weStartedRecording || (now - _lastObsCheck).TotalSeconds < 5) return;
        _lastObsCheck = now;
        bool? active;
        try { active = _recorder.IsRecordingActive(); }
        catch { active = null; }
        if (active != false) return;
        Log.Info("The recording was stopped in OBS");
        _weStartedRecording = false;
        _runningSinceUtc = null;
    }

    private void StopLocked()
    {
        try
        {
            var path = _recorder.StopRecording();
            if (path is not null) Organize(path, _meetingName, _recordingStarted);
        }
        catch (Exception ex) { Log.Error("Could not stop OBS recording", ex); }
        _weStartedRecording = false;
        _runningSinceUtc = null;
    }

    /// <summary>Stops the recording now (tray or indicator). The meeting goes on without a new prompt.</summary>
    public void StopRecordingNow()
    {
        lock (_gate)
        {
            if (!_weStartedRecording) return;
            Log.Info("Recording stopped by the user");
            StopLocked();
        }
    }

    /// <summary>Starts recording the current meeting, e.g. after it was dismissed.</summary>
    public void StartRecordingNow()
    {
        lock (_gate)
        {
            if (_weStartedRecording || !InMeeting) return;
        }
        Start();
    }

    public void PauseRecording()
    {
        lock (_gate)
        {
            if (!_weStartedRecording || _runningSinceUtc is not { } since) return;
            try { _recorder.PauseRecording(); }
            catch (Exception ex) { Log.Error("Could not pause OBS recording", ex); return; }
            _elapsedBeforePause += UtcNow() - since;
            _runningSinceUtc = null;
        }
    }

    public void ResumeRecording()
    {
        lock (_gate)
        {
            if (!_weStartedRecording || _runningSinceUtc is not null) return;
            try { _recorder.ResumeRecording(); }
            catch (Exception ex) { Log.Error("Could not resume OBS recording", ex); return; }
            _runningSinceUtc = UtcNow();
        }
    }

    private void Organize(string path, string meeting, DateTime started)
    {
        var cfg = _config.Recordings;
        var delay = OrganizeDelay;
        OrganizeTask = Task.Run(() =>
        {
            try { RecordingOrganizer.Organize(path, meeting, started, cfg, delay); }
            catch (Exception ex) { Log.Error($"Could not move the recording {path}", ex); }
        });
    }

    private void AskAndRecord(string meetingTitle)
    {
        PromptChoice choice;
        try
        {
            choice = _prompt.Ask("Teams meeting detected", $"{meetingTitle}\nRecord this meeting with OBS?",
                TimeSpan.FromSeconds(_config.PromptTimeoutSeconds));
        }
        catch (Exception ex)
        {
            Log.Error("Could not show the prompt", ex);
            return;
        }

        Log.Info($"User chose: {choice}");
        if (choice != PromptChoice.Record) return;
        lock (_gate)
        {
            if (!InMeeting) return; // meeting ended while the prompt was open
        }
        Start();
    }

    private void Start()
    {
        try
        {
            var started = _recorder.StartRecording();
            lock (_gate)
            {
                _weStartedRecording = started;
                _recordingStarted = DateTime.Now;
                _elapsedBeforePause = TimeSpan.Zero;
                _runningSinceUtc = started ? UtcNow() : null;
            }
        }
        catch (Exception ex)
        {
            Log.Error("Could not start OBS recording", ex);
        }
    }

    public void Run(CancellationToken token)
    {
        Log.Info($"Watching for Teams meeting windows: {string.Join(", ", _config.TitlePatterns)}");
        var interval = TimeSpan.FromSeconds(_config.PollIntervalSeconds);
        while (!token.IsCancellationRequested)
        {
            Tick(DateTime.UtcNow);
            token.WaitHandle.WaitOne(interval);
        }
    }
}
