using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;

namespace TeamsObsRecorder.Gui;

/// <summary>
/// Tray icon + settings window. With a <see cref="BackgroundService"/> it runs as the tray app;
/// without one (another copy is already running, or --settings) it only shows the settings window.
/// </summary>
public sealed class App : Application
{
    public static string ConfigPath { get; set; } = "";
    public static BackgroundService? Service { get; set; }

    private SettingsWindow? _settings;
    private TrayIcon? _tray;
    private RecordingIndicator? _indicator;
    private readonly DispatcherTimer _stateTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private string _trayIconName = "";

    /// <summary>A second copy of the app creates this file to ask the running copy to show its window.</summary>
    public static string ShowRequestPath =>
        Path.Combine(Path.GetDirectoryName(Path.GetFullPath(ConfigPath))!, "show-settings.request");

    public static WindowIcon LoadIcon(string name) =>
        new(AssetLoader.Open(new Uri($"avares://TeamsObsRecorder/Assets/{name}")));

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>().UsePlatformDetect().WithInterFont().LogToTrace();

    public override void Initialize()
    {
        Name = "Teams OBS Recorder";
        RequestedThemeVariant = ThemeVariant.Default; // follows the OS light/dark setting
        Styles.Add(new FluentTheme());
        Ui.AddThemeResources(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            if (Service is null)
            {
                desktop.ShutdownMode = ShutdownMode.OnMainWindowClose;
                desktop.MainWindow = new SettingsWindow(ConfigPath);
            }
            else
            {
                desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
                CreateTray(desktop, Service);
                if (Service.FirstRun) ShowSettings();
            }
        }
        base.OnFrameworkInitializationCompleted();
    }

    private void CreateTray(IClassicDesktopStyleApplicationLifetime desktop, BackgroundService service)
    {
        var settings = new NativeMenuItem("Settings…");
        settings.Click += (_, _) => ShowSettings();

        var watcher = service.Watcher;
        var recState = new NativeMenuItem("● Recording") { IsEnabled = false };
        var recStart = new NativeMenuItem("Start recording this meeting");
        recStart.Click += (_, _) => Task.Run(watcher.StartRecordingNow);
        var recPause = new NativeMenuItem("Pause recording");
        recPause.Click += (_, _) => Task.Run(() =>
        {
            if (watcher.IsRecordingPaused) watcher.ResumeRecording(); else watcher.PauseRecording();
        });
        var recStop = new NativeMenuItem("Stop recording");
        recStop.Click += (_, _) => Task.Run(watcher.StopRecordingNow);
        var recSeparator = new NativeMenuItemSeparator();

        var pause = new NativeMenuItem("Pause detection");
        pause.Click += (_, _) =>
        {
            service.Paused = !service.Paused;
            pause.Header = service.Paused ? "Resume detection" : "Pause detection";
            UpdateState();
        };

        var log = new NativeMenuItem("Open log file");
        log.Click += (_, _) => OpenFile(Path.Combine(AppConfig.DefaultDirectory, "teams-obs-recorder.log"));

        var exit = new NativeMenuItem("Exit");
        exit.Click += (_, _) =>
        {
            service.Stop();
            desktop.Shutdown();
        };

        var menu = new NativeMenu();
        menu.Items.Add(recState);
        menu.Items.Add(recStart);
        menu.Items.Add(recPause);
        menu.Items.Add(recStop);
        menu.Items.Add(recSeparator);
        menu.Items.Add(settings);
        menu.Items.Add(pause);
        menu.Items.Add(log);
        menu.Items.Add(new NativeMenuItemSeparator());
        menu.Items.Add(exit);

        _tray = new TrayIcon
        {
            Icon = LoadIcon("icon.png"),
            ToolTipText = "Teams OBS Recorder",
            Menu = menu,
            IsVisible = true,
        };
        _tray.Clicked += (_, _) => ShowSettings();
        TrayIcon.SetIcons(this, new TrayIcons { _tray });

        // Keeps the tray icon, its menu and the floating indicator in step with the recording.
        void UpdateState()
        {
            var recording = watcher.IsRecording;
            var paused = watcher.IsRecordingPaused;
            var meeting = watcher.MeetingTitle;
            var time = Ui.Clock(watcher.RecordingElapsed);

            recState.IsVisible = recPause.IsVisible = recStop.IsVisible = recording;
            recStart.IsVisible = !recording && meeting.Length > 0;
            recSeparator.IsVisible = recording || meeting.Length > 0;
            recState.Header = $"{(paused ? "❚❚ Paused" : "● Recording")} {time}{(meeting.Length > 0 ? $" · {meeting}" : "")}";
            recPause.Header = paused ? "Resume recording" : "Pause recording";

            var icon = recording ? "icon-recording.png" : service.Paused ? "icon-paused.png" : "icon.png";
            if (icon != _trayIconName)
            {
                _trayIconName = icon;
                _tray!.Icon = LoadIcon(icon);
            }
            _tray!.ToolTipText = recording
                ? $"Teams OBS Recorder: {(paused ? "paused" : "recording")} {time}"
                : service.Paused ? "Teams OBS Recorder (detection paused)" : "Teams OBS Recorder";

            var wantIndicator = recording && watcher.Config.ShowRecordingIndicator;
            if (wantIndicator && _indicator is null)
            {
                _indicator = new RecordingIndicator(watcher);
                _indicator.Closed += (_, _) => _indicator = null;
                _indicator.Show();
            }
            else if (!wantIndicator && _indicator is not null)
            {
                _indicator.Close();
            }

            if (File.Exists(ShowRequestPath))
            {
                try { File.Delete(ShowRequestPath); } catch { /* the other copy may delete it too */ }
                ShowSettings();
            }
        }

        _stateTimer.Tick += (_, _) => UpdateState();
        _stateTimer.Start();
        UpdateState();
    }

    private void ShowSettings()
    {
        if (_settings is null)
        {
            _settings = new SettingsWindow(ConfigPath, Service);
            _settings.Closed += (_, _) => _settings = null;
            _settings.Show();
        }
        _settings.Activate();
    }

    private static void OpenFile(string path)
    {
        try
        {
            if (!File.Exists(path)) File.WriteAllText(path, "");
            if (OperatingSystem.IsWindows())
                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            else
                Process.Start("xdg-open", path);
        }
        catch (Exception ex)
        {
            Log.Error($"Could not open {path}", ex);
        }
    }
}
