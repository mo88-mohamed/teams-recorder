using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Ellipse = Avalonia.Controls.Shapes.Ellipse;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;

namespace TeamsObsRecorder.Gui;

/// <summary>
/// The app's window: a dashboard with the live recording state, plus pages that edit config.json
/// and the start-at-login setting.
/// </summary>
public sealed class SettingsWindow : Window
{
    private readonly string _configPath;
    private readonly BackgroundService? _service;

    private readonly ToggleSwitch _startAtLogin = Ui.Toggle();
    private readonly ToggleSwitch _stopWhenEnds = Ui.Toggle();
    private readonly ToggleSwitch _showIndicator = Ui.Toggle();
    private readonly NumericUpDown _promptTimeout = Number(5, 3600);

    private readonly ToggleSwitch _detectMic = Ui.Toggle();
    private readonly ToggleSwitch _requireSound = Ui.Toggle();
    private readonly TextBox _titlePatterns = Text("*meeting*");
    private readonly TextBox _excludePatterns = Text("e.g. *notes*");
    private readonly TextBox _processNames = Text("empty = any app");
    private readonly TextBox _micApps = Text("*teams*");
    private readonly NumericUpDown _grace = Number(1, 600);
    private readonly NumericUpDown _poll = Number(1, 60);

    private readonly TextBox _host = Text("localhost");
    private readonly NumericUpDown _port = Number(1, 65535);
    private readonly TextBlock _hostWarning = new()
    {
        Text = "OBS is on another computer. This connection isn't encrypted, so only use it on a network you trust (or over a VPN).",
        Foreground = Ui.Red, FontSize = 12, TextWrapping = TextWrapping.Wrap, IsVisible = false,
    };
    private readonly TextBox _password = new() { PasswordChar = '•', Watermark = "Server password", Width = 280 };
    private readonly ToggleSwitch _launchObs = Ui.Toggle();
    private readonly TextBox _obsPath = Text("found automatically");
    private readonly NumericUpDown _obsWait = Number(1, 300);

    private readonly ToggleSwitch _organize = Ui.Toggle();
    private readonly TextBox _recFolder = Text("same folder OBS saves to");
    private readonly ToggleSwitch _groupByMeeting = Ui.Toggle();
    private readonly TextBox _fileName = Text("{date} {time} {meeting}");
    private readonly TextBlock _example = new() { FontSize = 13, TextWrapping = TextWrapping.Wrap, FontFamily = Ui.Mono };

    // Dashboard
    private readonly Ellipse _stateDot = new() { Width = 14, Height = 14, Fill = Ui.Grey };
    private readonly TextBlock _stateTitle = new() { FontSize = 22, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _stateDetail = new() { FontSize = 13, Opacity = 0.75, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _stateClock = new() { FontSize = 40, FontWeight = FontWeight.Light, FontFamily = Ui.Mono, IsVisible = false };
    private readonly Button _recStart = new() { Content = "● Start recording", Classes = { "accent" } };
    private readonly Button _recPause = new() { Content = "Pause" };
    private readonly Button _recStop = new() { Content = "■ Stop" };
    private readonly DispatcherTimer _clock = new() { Interval = TimeSpan.FromSeconds(1) };

    private readonly TextBlock _detectedSummary = new() { FontSize = 14, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _detectedDetails = new() { FontSize = 12, TextWrapping = TextWrapping.Wrap, FontFamily = Ui.Mono };
    private readonly DispatcherTimer _refresh = new() { Interval = TimeSpan.FromSeconds(2) };
    private IWindowLister? _lister;
    private IMicrophoneMonitor? _audio;
    private bool _detecting;

    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
    private readonly Button _testObs = new() { Content = "Test OBS connection" };
    private readonly Button _testToast = new() { Content = "Test notification + recording" };

    private readonly ContentControl _page = new();
    private readonly TextBlock _pageTitle = new() { FontSize = 28, FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 0, 0, 4) };
    private readonly List<(Button Button, Border Marker, string Title, Control Page)> _nav = new();

    public SettingsWindow(string configPath, BackgroundService? service = null)
    {
        _configPath = configPath;
        _service = service;
        Title = "Teams OBS Recorder";
        Icon = App.LoadIcon("icon.png");
        Width = 900;
        Height = 640;
        MinWidth = 720;
        MinHeight = 480;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        TransparencyLevelHint = new[] { WindowTransparencyLevel.Mica, WindowTransparencyLevel.None };
        this.Themed(BackgroundProperty, "PageBrush");
        Opened += (_, _) =>
        {
            // On Windows 11 the window gets the Mica backdrop, like the Settings app.
            if (ActualTransparencyLevel == WindowTransparencyLevel.Mica) Background = Brushes.Transparent;
        };

        var sidebar = new StackPanel { Spacing = 2, Margin = new Thickness(12, 16, 8, 12) };
        var brand = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Margin = new Thickness(8, 0, 0, 20) };
        brand.Children.Add(new Image { Source = new Avalonia.Media.Imaging.Bitmap(Avalonia.Platform.AssetLoader.Open(new Uri("avares://TeamsObsRecorder/Assets/icon.png"))), Width = 28, Height = 28 });
        brand.Children.Add(new TextBlock { Text = "Teams OBS Recorder", FontWeight = FontWeight.SemiBold, FontSize = 15, VerticalAlignment = VerticalAlignment.Center });
        sidebar.Children.Add(brand);

        AddPage(sidebar, Ui.IconHome, "Home", BuildHome());
        AddPage(sidebar, Ui.IconGeneral, "General", BuildGeneral());
        AddPage(sidebar, Ui.IconObs, "OBS", BuildObs());
        AddPage(sidebar, Ui.IconFolder, "Recordings", BuildRecordings());
        AddPage(sidebar, Ui.IconDetect, "Detection", BuildDetection());

        var version = new TextBlock
        {
            Text = $"Version {typeof(SettingsWindow).Assembly.GetName().Version?.ToString(3)}",
            FontSize = 12, Opacity = 0.55, Margin = new Thickness(12, 0, 0, 16), VerticalAlignment = VerticalAlignment.Bottom,
        };
        var side = new DockPanel { Width = 230 };
        DockPanel.SetDock(version, Dock.Bottom);
        side.Children.Add(version);
        side.Children.Add(sidebar);

        var save = new Button { Content = "Save", IsDefault = true, Classes = { "accent" }, MinWidth = 96, HorizontalContentAlignment = HorizontalAlignment.Center };
        var cancel = new Button { Content = "Close", IsCancel = true, MinWidth = 96, HorizontalContentAlignment = HorizontalAlignment.Center };
        save.Click += (_, _) => Save();
        cancel.Click += (_, _) => Close();
        _testObs.Click += async (_, _) => await TestObs();
        _testToast.Click += async (_, _) => await TestNotification();

        var footer = new DockPanel { Margin = new Thickness(0, 12, 0, 0) };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        buttons.Children.Add(cancel);
        buttons.Children.Add(save);
        DockPanel.SetDock(buttons, Dock.Right);
        footer.Children.Add(buttons);
        _status.Margin = new Thickness(0, 0, 16, 0);
        footer.Children.Add(_status);

        _page.Margin = new Thickness(0, 0, 18, 8);
        var scroller = new ScrollViewer { Content = _page, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        var main = new DockPanel { Margin = new Thickness(24, 20, 8, 16) };
        DockPanel.SetDock(_pageTitle, Dock.Top);
        DockPanel.SetDock(footer, Dock.Bottom);
        footer.Margin = new Thickness(0, 12, 16, 0);
        main.Children.Add(_pageTitle);
        main.Children.Add(footer);
        main.Children.Add(scroller);

        var root = new DockPanel();
        DockPanel.SetDock(side, Dock.Left);
        root.Children.Add(side);
        root.Children.Add(main);
        Content = root;

        Select(0);

        _refresh.Tick += async (_, _) => await RefreshDetection();
        _clock.Tick += (_, _) => UpdateRecordingState();
        Opened += async (_, _) =>
        {
            _refresh.Start();
            _clock.Start();
            UpdateRecordingState();
            await RefreshDetection();
        };
        Closed += (_, _) => { _refresh.Stop(); _clock.Stop(); };

        LoadForm();
    }

    private void AddPage(StackPanel sidebar, string icon, string title, Control page)
    {
        var index = _nav.Count;
        var marker = new Border { Width = 3, Height = 16, CornerRadius = new CornerRadius(2), Background = Ui.Red, IsVisible = false, VerticalAlignment = VerticalAlignment.Center };
        var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        content.Children.Add(marker);
        content.Children.Add(Ui.Icon(icon));
        content.Children.Add(new TextBlock { Text = title, FontSize = 14, VerticalAlignment = VerticalAlignment.Center });
        var button = new Button
        {
            Content = content, Background = Brushes.Transparent, BorderThickness = new Thickness(0),
            HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Left,
            Padding = new Thickness(6, 9), CornerRadius = new CornerRadius(6),
        };
        button.Click += (_, _) => Select(index);
        sidebar.Children.Add(button);
        _nav.Add((button, marker, title, page));
    }

    private void Select(int index)
    {
        for (var i = 0; i < _nav.Count; i++)
        {
            var (button, marker, _, _) = _nav[i];
            marker.IsVisible = i == index;
            if (i == index) button.Themed(BackgroundProperty, "NavSelectedBrush");
            else button.Background = Brushes.Transparent;
        }
        _pageTitle.Text = index == 0 ? "Home" : _nav[index].Title;
        _page.Content = _nav[index].Page;
    }

    private Control BuildHome()
    {
        var head = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        _stateDot.VerticalAlignment = VerticalAlignment.Center;
        head.Children.Add(_stateDot);
        head.Children.Add(_stateTitle);

        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 8, 0, 0) };
        actions.Children.Add(_recStart);
        actions.Children.Add(_recPause);
        actions.Children.Add(_recStop);
        _recStart.Click += async (_, _) => await RunOnWatcher(w => w.StartRecordingNow());
        _recPause.Click += async (_, _) => await RunOnWatcher(w =>
        {
            if (w.IsRecordingPaused) w.ResumeRecording(); else w.PauseRecording();
        });
        _recStop.Click += async (_, _) => await RunOnWatcher(w => w.StopRecordingNow());

        var hero = new StackPanel { Spacing = 6 };
        hero.Children.Add(head);
        hero.Children.Add(_stateDetail);
        hero.Children.Add(_stateClock);
        hero.Children.Add(actions);

        var tests = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        tests.Children.Add(_testObs);
        tests.Children.Add(_testToast);
        var checks = new StackPanel { Spacing = 10 };
        checks.Children.Add(Ui.Caption("Check that OBS answers, and that the notification shows. Choosing Record on it makes a 5-second test clip."));
        checks.Children.Add(tests);

        var detected = new StackPanel { Spacing = 8 };
        detected.Children.Add(Ui.Caption("What the app sees right now, with the settings as they are on these pages. Start a call to check that it's detected."));
        detected.Children.Add(_detectedSummary);
        detected.Children.Add(new Expander { Header = "Details", Content = _detectedDetails, HorizontalAlignment = HorizontalAlignment.Stretch });

        var page = new StackPanel();
        page.Children.Add(Ui.Card(hero, new Thickness(24, 20)));
        page.Children.Add(Ui.Section("Quick checks"));
        page.Children.Add(Ui.Card(checks));
        page.Children.Add(Ui.Section("Detected right now"));
        page.Children.Add(Ui.Card(detected));
        return page;
    }

    private Control BuildGeneral()
    {
        var page = new StackPanel();
        page.Children.Add(Ui.Section("Startup"));
        page.Children.Add(Ui.Group(
            Ui.Setting("Start when I sign in", "Runs in the background and sits in the system tray", _startAtLogin)));
        page.Children.Add(Ui.Section("While recording"));
        page.Children.Add(Ui.Group(
            Ui.Setting("Show the recording indicator", "A small bar with the time and Pause / Stop buttons, always on top", _showIndicator),
            Ui.Setting("Stop when the meeting ends", "Only stops recordings this app started", _stopWhenEnds),
            Ui.Setting("Answer the prompt within (seconds)", "No answer counts as Dismiss", _promptTimeout)));
        return page;
    }

    private Control BuildObs()
    {
        var browse = new Button { Content = "Browse…", Margin = new Thickness(8, 0, 0, 0) };
        browse.Click += async (_, _) => await BrowseForObs();
        var obsPathRow = new DockPanel();
        DockPanel.SetDock(browse, Dock.Right);
        obsPathRow.Children.Add(browse);
        obsPathRow.Children.Add(_obsPath);

        _host.Width = 280;
        _hostWarning.IsVisible = true;
        var hostWarningCard = Ui.Card(_hostWarning);
        hostWarningCard.IsVisible = false;
        _host.TextChanged += (_, _) => hostWarningCard.IsVisible = !Secrets.IsLocalHost(_host.Text ?? "");

        var page = new StackPanel();
        page.Children.Add(Ui.Section("WebSocket connection"));
        page.Children.Add(Ui.Group(
            Ui.Setting("Password", "From OBS > Tools > WebSocket Server Settings. Saved encrypted on Windows", _password),
            Ui.Setting("Host", "localhost when OBS runs on this computer", _host),
            hostWarningCard,
            Ui.Setting("Port", null, _port),
            Ui.Card(TestObsRow())));
        page.Children.Add(Ui.Section("Starting OBS"));
        page.Children.Add(Ui.Group(
            Ui.Setting("Start OBS if it isn't running", "OBS starts minimised to the tray when you choose Record", _launchObs),
            Ui.WideSetting("OBS program", null, obsPathRow),
            Ui.Setting("Wait for OBS to start (seconds)", null, _obsWait)));
        return page;
    }

    /// <summary>A second Test button on the OBS page, next to the fields it tests.</summary>
    private Control TestObsRow()
    {
        var test = new Button { Content = "Test connection" };
        test.Click += async (_, _) => await TestObs();
        var row = new DockPanel();
        DockPanel.SetDock(test, Dock.Right);
        row.Children.Add(test);
        row.Children.Add(new TextBlock { Text = "Check that OBS answers with these settings", VerticalAlignment = VerticalAlignment.Center, FontSize = 14 });
        return row;
    }

    private Control BuildRecordings()
    {
        var browseFolder = new Button { Content = "Browse…", Margin = new Thickness(8, 0, 0, 0) };
        browseFolder.Click += async (_, _) => await BrowseForFolder();
        var folderRow = new DockPanel();
        DockPanel.SetDock(browseFolder, Dock.Right);
        folderRow.Children.Add(browseFolder);
        folderRow.Children.Add(_recFolder);

        foreach (var box in new[] { _recFolder, _fileName }) box.TextChanged += (_, _) => UpdateExample();
        foreach (var box in new[] { _organize, _groupByMeeting }) box.IsCheckedChanged += (_, _) => UpdateExample();

        var example = new StackPanel { Spacing = 6 };
        example.Children.Add(Ui.Caption("A recording of “Weekly Sync” will be saved as"));
        example.Children.Add(_example);

        var page = new StackPanel();
        page.Children.Add(Ui.Section("Names and folders"));
        page.Children.Add(Ui.Group(
            Ui.Setting("Rename and organize recordings", "When a recording this app started stops", _organize),
            Ui.WideSetting("Save recordings in", "Empty = the folder OBS already saves to", folderRow),
            Ui.Setting("One folder per meeting", "Recurring meetings end up together", _groupByMeeting),
            Ui.WideSetting("File name", "{meeting} = meeting or caller name, {date} = 2026-10-07, {time} = 14-30", _fileName)));
        page.Children.Add(Ui.Section("Preview"));
        page.Children.Add(Ui.Card(example));
        return page;
    }

    private Control BuildDetection()
    {
        var page = new StackPanel();
        page.Children.Add(Ui.Section("Calls"));
        page.Children.Add(Ui.Group(
            Ui.Setting("Detect calls by microphone", "Teams using the microphone counts as a call. Catches 1:1 and group calls", _detectMic),
            Ui.Setting("Also require sound", "Only count it when Teams also plays sound (mic + speakers together)", _requireSound),
            Ui.WideSetting("Apps using the microphone", "Comma-separated, * matches anything", _micApps)));
        page.Children.Add(Ui.Section("Meeting windows"));
        page.Children.Add(Ui.Group(
            Ui.WideSetting("Meeting window titles", "Comma-separated, * matches anything", _titlePatterns),
            Ui.WideSetting("Ignore window titles", null, _excludePatterns),
            Ui.WideSetting("Teams process names", "Empty = windows of any app count", _processNames)));
        page.Children.Add(Ui.Section("Timing"));
        page.Children.Add(Ui.Group(
            Ui.Setting("Meeting ends after (seconds)", "How long nothing must be detected", _grace),
            Ui.Setting("Check every (seconds)", null, _poll)));
        return page;
    }

    private async Task RunOnWatcher(Action<MeetingWatcher> action)
    {
        if (_service is null) return;
        foreach (var b in new[] { _recStart, _recPause, _recStop }) b.IsEnabled = false;
        try { await Task.Run(() => action(_service.Watcher)); }
        catch (Exception ex) { SetStatus(ex.Message, error: true); }
        UpdateRecordingState();
    }

    /// <summary>Refreshes the Home card from the running app every second.</summary>
    private void UpdateRecordingState()
    {
        if (_service is null)
        {
            _stateDot.Fill = Ui.Grey;
            _stateTitle.Text = "Settings only";
            _stateDetail.Text = "The background app isn't running in this window, so live recording controls aren't available here.";
            _stateClock.IsVisible = false;
            foreach (var b in new[] { _recStart, _recPause, _recStop }) b.IsVisible = false;
            return;
        }

        var w = _service.Watcher;
        var recording = w.IsRecording;
        var meeting = w.MeetingTitle;
        _stateClock.IsVisible = recording;
        _recStart.IsVisible = !recording && meeting.Length > 0;
        _recPause.IsVisible = _recStop.IsVisible = recording;
        foreach (var b in new[] { _recStart, _recPause, _recStop }) b.IsEnabled = true;

        if (recording)
        {
            var paused = w.IsRecordingPaused;
            _stateDot.Fill = paused ? Ui.Amber : Ui.Red;
            _stateTitle.Text = paused ? "Recording paused" : "Recording";
            _stateDetail.Text = meeting.Length > 0 ? meeting : "Teams call";
            _stateClock.Text = Ui.Clock(w.RecordingElapsed);
            _recPause.Content = paused ? "▶ Resume" : "❚❚ Pause";
        }
        else if (meeting.Length > 0)
        {
            _stateDot.Fill = Ui.Green;
            _stateTitle.Text = "In a meeting, not recording";
            _stateDetail.Text = meeting;
        }
        else if (_service.Paused)
        {
            _stateDot.Fill = Ui.Amber;
            _stateTitle.Text = "Detection paused";
            _stateDetail.Text = "Resume it from the tray icon menu.";
        }
        else
        {
            _stateDot.Fill = Ui.Grey;
            _stateTitle.Text = "Watching for Teams meetings";
            _stateDetail.Text = "You'll get a Record / Dismiss notification when a meeting or call starts.";
        }
    }

    private static NumericUpDown Number(int min, int max) => new()
    {
        Minimum = min, Maximum = max, Increment = 1, FormatString = "0", Width = 140,
    };

    private static TextBox Text(string watermark) => new() { Watermark = watermark };

    private static string Join(IEnumerable<string> items) => string.Join(", ", items);

    private static List<string> Split(string? text) =>
        (text ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

    private void LoadForm()
    {
        AppConfig config;
        try
        {
            config = AppConfig.Load(_configPath);
        }
        catch (Exception ex)
        {
            config = new AppConfig();
            SetStatus($"Could not read {_configPath} ({ex.Message}); showing defaults.", error: true);
        }

        try { _startAtLogin.IsChecked = Autostart.IsInstalled(); }
        catch (Exception ex) { Log.Error("Could not read the start-at-login setting", ex); }
        _stopWhenEnds.IsChecked = config.StopRecordingWhenMeetingEnds;
        _showIndicator.IsChecked = config.ShowRecordingIndicator;
        _promptTimeout.Value = (decimal)config.PromptTimeoutSeconds;

        _detectMic.IsChecked = config.DetectMicrophoneUse;
        _requireSound.IsChecked = config.RequireSoundWithMicrophone;
        _titlePatterns.Text = Join(config.TitlePatterns);
        _excludePatterns.Text = Join(config.ExcludeTitlePatterns);
        _processNames.Text = Join(config.ProcessNames);
        _micApps.Text = Join(config.MicrophoneApps);
        _grace.Value = (decimal)config.MeetingEndGraceSeconds;
        _poll.Value = (decimal)config.PollIntervalSeconds;

        _host.Text = config.Obs.Host;
        _port.Value = config.Obs.Port;
        _password.Text = config.Obs.Password;
        _launchObs.IsChecked = config.Obs.LaunchIfNotRunning;
        _obsPath.Text = config.Obs.Executable;
        _obsWait.Value = (decimal)config.Obs.StartupWaitSeconds;

        _organize.IsChecked = config.Recordings.Organize;
        _recFolder.Text = config.Recordings.Folder;
        _groupByMeeting.IsChecked = config.Recordings.GroupByMeeting;
        _fileName.Text = config.Recordings.FileName;
        UpdateExample();
    }

    private AppConfig ReadForm()
    {
        var titles = Split(_titlePatterns.Text);
        if (titles.Count == 0 && _detectMic.IsChecked != true)
            throw new InvalidOperationException("Add at least one meeting window title, or turn on microphone detection.");

        return new AppConfig
        {
            StopRecordingWhenMeetingEnds = _stopWhenEnds.IsChecked == true,
            ShowRecordingIndicator = _showIndicator.IsChecked == true,
            PromptTimeoutSeconds = (double)(_promptTimeout.Value ?? 60),
            DetectMicrophoneUse = _detectMic.IsChecked == true,
            RequireSoundWithMicrophone = _requireSound.IsChecked == true,
            TitlePatterns = titles,
            ExcludeTitlePatterns = Split(_excludePatterns.Text),
            ProcessNames = Split(_processNames.Text),
            MicrophoneApps = Split(_micApps.Text),
            MeetingEndGraceSeconds = (double)(_grace.Value ?? 10),
            PollIntervalSeconds = (double)(_poll.Value ?? 2),
            Recordings = ReadRecordings(),
            Obs = new ObsConfig
            {
                Host = string.IsNullOrWhiteSpace(_host.Text) ? "localhost" : _host.Text.Trim(),
                Port = (int)(_port.Value ?? 4455),
                Password = _password.Text ?? "",
                LaunchIfNotRunning = _launchObs.IsChecked == true,
                Executable = _obsPath.Text?.Trim() ?? "",
                StartupWaitSeconds = (double)(_obsWait.Value ?? 20),
            },
        };
    }

    private RecordingsConfig ReadRecordings() => new()
    {
        Organize = _organize.IsChecked == true,
        Folder = _recFolder.Text?.Trim() ?? "",
        GroupByMeeting = _groupByMeeting.IsChecked == true,
        FileName = string.IsNullOrWhiteSpace(_fileName.Text) ? "{date} {time} {meeting}" : _fileName.Text.Trim(),
    };

    private void UpdateExample()
    {
        var cfg = ReadRecordings();
        foreach (var c in new Control[] { _recFolder, _groupByMeeting, _fileName }) c.IsEnabled = cfg.Organize;
        if (!cfg.Organize)
        {
            _example.Text = "Recordings keep the name and folder OBS gives them";
            return;
        }
        var folder = RecordingOrganizer.TargetFolder(cfg, "Weekly Sync", "<OBS recordings folder>");
        var file = RecordingOrganizer.FileStem(cfg, "Weekly Sync", new DateTime(2026, 10, 7, 14, 30, 0)) + ".mkv";
        _example.Text = Path.Combine(folder, file);
    }

    private async Task BrowseForFolder()
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Where should recordings be saved?",
            AllowMultiple = false,
        });
        var path = folders.FirstOrDefault()?.TryGetLocalPath();
        if (path is not null) _recFolder.Text = path;
    }

    private void Save()
    {
        try
        {
            ReadForm().Save(_configPath);
            var wantAutostart = _startAtLogin.IsChecked == true;
            if (wantAutostart != Autostart.IsInstalled())
                Log.Info(wantAutostart ? Autostart.Install() : Autostart.Uninstall());
            Log.Info("Settings saved");
            SetStatus("Saved. Changes apply right away.");
        }
        catch (Exception ex)
        {
            SetStatus($"Not saved: {ex.Message}", error: true);
        }
    }

    private async Task TestObs()
    {
        ObsConfig obsConfig;
        try { obsConfig = ReadForm().Obs; }
        catch (Exception ex) { SetStatus(ex.Message, error: true); return; }

        _testObs.IsEnabled = false;
        SetStatus("Connecting to OBS…");
        try
        {
            var version = await Task.Run(() =>
            {
                using var obs = new ObsRecorder(obsConfig).Connect();
                return obs.Request("GetVersion")["obsVersion"]?.ToString();
            });
            SetStatus($"Connected to OBS {version}.");
        }
        catch (Exception ex)
        {
            SetStatus($"Could not connect to OBS: {ex.Message}", error: true);
        }
        finally
        {
            _testObs.IsEnabled = true;
        }
    }

    /// <summary>Shows the real prompt; on Record, makes a 5-second test recording in OBS.</summary>
    private async Task TestNotification()
    {
        ObsConfig obsConfig;
        RecordingsConfig recordingsConfig;
        try
        {
            var form = ReadForm();
            obsConfig = form.Obs;
            recordingsConfig = form.Recordings;
        }
        catch (Exception ex) { SetStatus(ex.Message, error: true); return; }

        _testToast.IsEnabled = false;
        SetStatus("Notification shown. Click Record or Dismiss on it.");
        try
        {
            var choice = await Task.Run(() =>
                Program.CreatePrompt().Ask("Teams meeting detected", "Test: record 5 seconds with OBS?", TimeSpan.FromSeconds(30)));
            if (choice != PromptChoice.Record)
            {
                SetStatus("The notification works. You chose Dismiss, so nothing was recorded.");
                return;
            }

            SetStatus("You chose Record. Recording a 5-second test in OBS…");
            var recorder = new ObsRecorder(obsConfig);
            var started = await Task.Run(recorder.StartRecording);
            if (!started)
            {
                SetStatus("The notification works. OBS was already recording, so the test left it alone.");
                return;
            }
            var startedAt = DateTime.Now;
            await Task.Delay(TimeSpan.FromSeconds(5));
            var path = await Task.Run(recorder.StopRecording);
            if (path is null || !recordingsConfig.Organize)
            {
                SetStatus("Done: OBS recorded a 5-second test clip. You'll find it in OBS under File > Show Recordings.");
                return;
            }
            SetStatus("Recorded. Moving the test clip to your recordings folder…");
            var saved = await Task.Run(() => RecordingOrganizer.Organize(path, "Test recording", startedAt, recordingsConfig));
            SetStatus($"Done: the 5-second test clip is saved as {saved}");
        }
        catch (Exception ex)
        {
            SetStatus($"Test failed: {ex.Message}", error: true);
        }
        finally
        {
            _testToast.IsEnabled = true;
        }
    }

    /// <summary>Runs detection with the settings as currently typed and shows the result.</summary>
    private async Task RefreshDetection()
    {
        if (_detecting) return;
        AppConfig config;
        try { config = ReadForm(); }
        catch { return; }

        _detecting = true;
        try
        {
            var result = await Task.Run(() =>
            {
                _lister ??= Program.CreateWindowLister();
                _audio ??= Program.CreateMicrophoneMonitor();
                return Detection.Run(_lister, _audio, config);
            });
            ShowDetection(result, config);
        }
        catch (Exception ex)
        {
            _detectedSummary.Text = "Could not check";
            _detectedDetails.Text = ex.Message;
        }
        finally
        {
            _detecting = false;
        }
    }

    private void ShowDetection(Detection d, AppConfig config)
    {
        if (d.MeetingWindows.Count > 0)
            _detectedSummary.Text = "✔ Meeting detected (window title)";
        else if (d.CallByAudio)
            _detectedSummary.Text = "✔ Call detected (Teams is using the microphone)";
        else if (d.MicrophoneMatches.Count > 0)
            _detectedSummary.Text = "Teams is using the microphone but not playing sound, so it doesn't count yet";
        else
            _detectedSummary.Text = "No meeting or call detected";

        static string List(IEnumerable<string> items, ICollection<string> matches) =>
            items.Any() ? string.Join("\n", items.Select(i => (matches.Contains(i) ? "  ✔ " : "    ") + i)) : "    (none)";

        var teamsWindows = d.Windows
            .Where(w => w.Title.Contains("teams", StringComparison.OrdinalIgnoreCase) ||
                        w.ProcessName.Contains("teams", StringComparison.OrdinalIgnoreCase))
            .Select(w => $"{w.Title}  [{(w.ProcessName.Length > 0 ? w.ProcessName : "?")}]");
        var meetingTitles = d.MeetingWindows.Select(w => $"{w.Title}  [{(w.ProcessName.Length > 0 ? w.ProcessName : "?")}]").ToList();

        var text = "Teams windows:\n" + List(teamsWindows, meetingTitles);
        if (config.DetectMicrophoneUse)
        {
            text += "\n\nApps using the microphone:\n" + List(d.MicrophoneApps, d.MicrophoneMatches);
            text += "\n\nApps playing sound:\n" + List(d.SoundApps, d.SoundMatches);
        }
        else
        {
            text += "\n\nMicrophone detection is off.";
        }
        if (d.Errors.Count > 0) text += "\n\nProblems:\n    " + string.Join("\n    ", d.Errors);
        _detectedDetails.Text = text;
    }

    private async Task BrowseForObs()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Select the OBS program",
            AllowMultiple = false,
            FileTypeFilter = OperatingSystem.IsWindows()
                ? new[] { new FilePickerFileType("OBS") { Patterns = new[] { "obs64.exe", "*.exe" } } }
                : null,
        });
        var path = files.FirstOrDefault()?.TryGetLocalPath();
        if (path is not null) _obsPath.Text = path;
    }

    private void SetStatus(string text, bool error = false)
    {
        _status.Text = text;
        if (error) _status.Foreground = Ui.Red;
        else _status.ClearValue(TextBlock.ForegroundProperty);
    }
}
