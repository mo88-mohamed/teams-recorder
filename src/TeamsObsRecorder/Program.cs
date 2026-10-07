using System.Runtime.InteropServices;
using Avalonia;
using TeamsObsRecorder.Gui;
using TeamsObsRecorder.Platform;

namespace TeamsObsRecorder;

public static class Program
{
    private const string Usage = """
        Teams OBS Recorder: offers to record Microsoft Teams meetings with OBS.

        Usage: TeamsObsRecorder [option]
          (no option)            run with a tray icon and watch for meetings; if it is
                                 already running, open the settings window instead
          --settings             open the settings window
          --headless             watch for meetings without a tray icon or windows
          --config <path>        use this config file instead of the default
          --list-windows         print open windows (marking meeting matches) and apps using the mic
          --test-toast           show the Record/Dismiss prompt once
          --test-obs             check the OBS connection (starts OBS if needed)
          --install-autostart    start automatically at login
          --uninstall-autostart  stop starting at login
          --help                 show this help
        """;

    [DllImport("kernel32.dll")]
    private static extern bool AttachConsole(int processId);

    [STAThread]
    public static int Main(string[] args)
    {
        // The Windows build has no console of its own; reuse the terminal it was started from.
        if (OperatingSystem.IsWindows() && args.Length > 0) AttachConsole(-1);

        var configPath = Path.Combine(AppConfig.DefaultDirectory, "config.json");
        var command = "";
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] == "--config" && i + 1 < args.Length) configPath = args[++i];
            else command = args[i];
        }

        Log.Init(Path.Combine(AppConfig.DefaultDirectory, "teams-obs-recorder.log"));
        try
        {
            return Run(command, configPath);
        }
        catch (Exception ex)
        {
            Log.Error("Fatal error", ex);
            return 1;
        }
    }

    private static int Run(string command, string configPath)
    {
        switch (command)
        {
            case "--help" or "-h":
                Console.WriteLine(Usage);
                return 0;
            case "--install-autostart":
                Console.WriteLine(Autostart.Install());
                return 0;
            case "--uninstall-autostart":
                Console.WriteLine(Autostart.Uninstall());
                return 0;
        }

        if (command == "--settings") return RunGui(configPath, service: null);

        var firstRun = !File.Exists(configPath);
        var config = AppConfig.Load(configPath);
        var lister = CreateWindowLister();
        var prompt = CreatePrompt();
        var recorder = new ObsRecorder(config.Obs);
        var microphone = CreateMicrophoneMonitor();

        switch (command)
        {
            case "--list-windows":
                var windows = lister.List();
                var matches = MeetingMatcher.FindMeetings(windows, config).Select(w => w.Id).ToHashSet();
                foreach (var w in windows)
                {
                    var mark = matches.Contains(w.Id) ? "MATCH" : "     ";
                    var proc = w.ProcessName.Length > 0 ? w.ProcessName : "?";
                    Console.WriteLine($"{mark}  {proc,-20} {w.Title}");
                }
                var micApps = microphone.AppsUsingMicrophone();
                var micMatches = MeetingMatcher.FindMicrophoneApps(micApps, config).ToHashSet();
                Console.WriteLine();
                Console.WriteLine(micApps.Count == 0 ? "No app is using the microphone." : "Apps using the microphone:");
                foreach (var app in micApps.Distinct())
                    Console.WriteLine($"{(micMatches.Contains(app) ? "MATCH" : "     ")}  {app}");
                return 0;
            case "--test-toast":
                Console.WriteLine($"Choice: {prompt.Ask("Teams meeting detected", "Test prompt", TimeSpan.FromSeconds(30))}");
                return 0;
            case "--test-obs":
                using (var obs = recorder.Connect())
                    Console.WriteLine($"Connected to OBS {obs.Request("GetVersion")["obsVersion"]}");
                return 0;
            case "" or "--headless":
                break;
            default:
                Console.WriteLine(Usage);
                return 2;
        }

        using var instance = new SingleInstance(Path.GetDirectoryName(Path.GetFullPath(configPath))!);
        if (!instance.IsFirst)
        {
            if (command == "--headless")
            {
                Log.Info("Already running; exiting");
                return 0;
            }
            // Starting it again (e.g. from the Start menu) opens the settings of the running copy.
            return RunGui(configPath, service: null);
        }

        if (command == "")
        {
            var service = new BackgroundService(configPath, firstRun, lister, prompt, microphone);
            service.Start();
            return RunGui(configPath, service);
        }

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
        AppDomain.CurrentDomain.ProcessExit += (_, _) => cts.Cancel();
        new MeetingWatcher(config, lister, prompt, recorder, microphone).Run(cts.Token);
        return 0;
    }

    private static int RunGui(string configPath, BackgroundService? service)
    {
        App.ConfigPath = configPath;
        App.Service = service;
        try
        {
            App.BuildAvaloniaApp().StartWithClassicDesktopLifetime(Array.Empty<string>());
        }
        catch (Exception ex) when (service is not null)
        {
            // No desktop session (e.g. started over SSH): keep watching without the tray icon.
            Log.Error("Could not start the tray icon; continuing without it", ex);
            service.Wait();
        }
        service?.Stop();
        return 0;
    }

    internal static IWindowLister CreateWindowLister()
    {
        if (OperatingSystem.IsWindows()) return new WindowsWindowLister();
        if (OperatingSystem.IsLinux()) return new LinuxWindowLister();
        throw new PlatformNotSupportedException("Only Windows and Linux are supported");
    }

    internal static IMicrophoneMonitor CreateMicrophoneMonitor()
    {
        if (OperatingSystem.IsWindows()) return new WindowsMicrophoneMonitor();
        return new LinuxMicrophoneMonitor();
    }

    internal static IPrompt CreatePrompt()
    {
#if WINDOWS
        return new WindowsToastPrompt();
#else
        if (OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Use the Windows build (net8.0-windows) for toast notifications");
        return new LinuxPrompt();
#endif
    }
}
