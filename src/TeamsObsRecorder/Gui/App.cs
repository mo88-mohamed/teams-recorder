using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;

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

    public static WindowIcon LoadIcon(string name) =>
        new(AssetLoader.Open(new Uri($"avares://TeamsObsRecorder/Assets/{name}")));

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>().UsePlatformDetect().WithInterFont().LogToTrace();

    public override void Initialize()
    {
        Name = "Teams OBS Recorder";
        RequestedThemeVariant = ThemeVariant.Default; // follows the OS light/dark setting
        Styles.Add(new FluentTheme());
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

        var pause = new NativeMenuItem("Pause detection");
        pause.Click += (_, _) =>
        {
            service.Paused = !service.Paused;
            pause.Header = service.Paused ? "Resume detection" : "Pause detection";
            _tray!.Icon = LoadIcon(service.Paused ? "icon-paused.png" : "icon.png");
            _tray.ToolTipText = service.Paused ? "Teams OBS Recorder (paused)" : "Teams OBS Recorder";
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
    }

    private void ShowSettings()
    {
        if (_settings is null)
        {
            _settings = new SettingsWindow(ConfigPath);
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
