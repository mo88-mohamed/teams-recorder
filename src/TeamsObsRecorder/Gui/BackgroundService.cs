namespace TeamsObsRecorder.Gui;

/// <summary>
/// Runs the meeting watcher on a background thread and reloads config.json whenever it changes
/// on disk, so settings saved from the settings window (this process or a second one) apply
/// without a restart.
/// </summary>
public sealed class BackgroundService
{
    private readonly string _configPath;
    private readonly MeetingWatcher _watcher;
    private readonly ObsRecorder _recorder;
    private readonly CancellationTokenSource _cts = new();
    private readonly Thread _thread;
    private DateTime _configStamp;

    public bool FirstRun { get; }

    public bool Paused
    {
        get => _watcher.Paused;
        set
        {
            _watcher.Paused = value;
            Log.Info(value ? "Detection paused" : "Detection resumed");
        }
    }

    public BackgroundService(string configPath, bool firstRun, IWindowLister lister, IPrompt prompt,
        IMicrophoneMonitor microphone)
    {
        _configPath = configPath;
        FirstRun = firstRun;
        var config = AppConfig.Load(configPath);
        _configStamp = Stamp();
        _recorder = new ObsRecorder(config.Obs);
        _watcher = new MeetingWatcher(config, lister, prompt, _recorder, microphone);
        _thread = new Thread(Loop) { IsBackground = true, Name = "MeetingWatcher" };
    }

    public void Start() => _thread.Start();

    public void Stop() => _cts.Cancel();

    /// <summary>Blocks until Stop is called (used when no desktop is available).</summary>
    public void Wait() => _thread.Join();

    private void Loop()
    {
        Log.Info($"Watching for Teams meetings and calls: {string.Join(", ", _watcher.Config.TitlePatterns)}");
        while (!_cts.IsCancellationRequested)
        {
            ReloadIfChanged();
            _watcher.Tick(DateTime.UtcNow);
            var interval = Math.Max(0.5, _watcher.Config.PollIntervalSeconds);
            _cts.Token.WaitHandle.WaitOne(TimeSpan.FromSeconds(interval));
        }
    }

    private DateTime Stamp() => File.Exists(_configPath) ? File.GetLastWriteTimeUtc(_configPath) : default;

    private void ReloadIfChanged()
    {
        var stamp = Stamp();
        if (stamp == _configStamp) return;
        _configStamp = stamp;
        try
        {
            var config = AppConfig.Load(_configPath);
            _watcher.Config = config;
            _recorder.Config = config.Obs;
            Log.Info("Settings reloaded");
        }
        catch (Exception ex)
        {
            Log.Error("Could not reload settings; keeping the previous ones", ex);
        }
    }
}
