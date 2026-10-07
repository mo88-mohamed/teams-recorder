using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;

namespace TeamsObsRecorder.Gui;

/// <summary>Edits config.json and the start-at-login setting.</summary>
public sealed class SettingsWindow : Window
{
    private readonly string _configPath;

    private readonly CheckBox _startAtLogin = new() { Content = "Start automatically when I sign in" };
    private readonly CheckBox _stopWhenEnds = new() { Content = "Stop recording when the meeting or call ends" };
    private readonly NumericUpDown _promptTimeout = Number(5, 3600);

    private readonly CheckBox _detectMic = new() { Content = "Detect calls when Teams uses the microphone (1:1 and group calls)" };
    private readonly CheckBox _requireSound = new() { Content = "Only when Teams is also playing sound (mic + speakers together)" };
    private readonly TextBox _titlePatterns = Text("*meeting*");
    private readonly TextBox _excludePatterns = Text("e.g. *notes*");
    private readonly TextBox _processNames = Text("empty = any app");
    private readonly TextBox _micApps = Text("*teams*");
    private readonly NumericUpDown _grace = Number(1, 600);
    private readonly NumericUpDown _poll = Number(1, 60);

    private readonly TextBox _host = Text("localhost");
    private readonly NumericUpDown _port = Number(1, 65535);
    private readonly TextBox _password = new() { PasswordChar = '•', Watermark = "from OBS > Tools > WebSocket Server Settings" };
    private readonly CheckBox _launchObs = new() { Content = "Start OBS if it isn't running" };
    private readonly TextBox _obsPath = Text("found automatically");
    private readonly NumericUpDown _obsWait = Number(1, 300);

    private readonly CheckBox _organize = new() { Content = "Rename and organize recordings when they stop" };
    private readonly TextBox _recFolder = Text("same folder OBS saves to");
    private readonly CheckBox _groupByMeeting = new() { Content = "Put each meeting in its own folder (recurring meetings stay together)" };
    private readonly TextBox _fileName = Text("{date} {time} {meeting}");
    private readonly TextBlock _example = new() { FontSize = 12, Opacity = 0.8, TextWrapping = TextWrapping.Wrap };

    private readonly TextBlock _detectedSummary = new() { FontSize = 15, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _detectedDetails = new() { FontSize = 12, TextWrapping = TextWrapping.Wrap, FontFamily = new FontFamily("Consolas, DejaVu Sans Mono, monospace") };
    private readonly DispatcherTimer _refresh = new() { Interval = TimeSpan.FromSeconds(2) };
    private IWindowLister? _lister;
    private IMicrophoneMonitor? _audio;
    private bool _detecting;

    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) };
    private readonly Button _testObs = new() { Content = "Test OBS connection" };
    private readonly Button _testToast = new() { Content = "Test notification + recording" };

    public SettingsWindow(string configPath)
    {
        _configPath = configPath;
        Title = "Teams OBS Recorder – Settings";
        Icon = App.LoadIcon("icon.png");
        Width = 620;
        Height = 470;
        MinWidth = 520;
        MinHeight = 360;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        var browse = new Button { Content = "Browse…" };
        browse.Click += async (_, _) => await BrowseForObs();
        var obsPathRow = new DockPanel();
        DockPanel.SetDock(browse, Dock.Right);
        browse.Margin = new Thickness(8, 0, 0, 0);
        obsPathRow.Children.Add(browse);
        obsPathRow.Children.Add(_obsPath);

        var general = Page();
        general.Children.Add(_startAtLogin);
        general.Children.Add(_stopWhenEnds);
        general.Children.Add(Row("Prompt timeout (seconds)", _promptTimeout));

        var detection = Page();
        detection.Children.Add(_detectMic);
        _requireSound.Margin = new Thickness(28, -4, 0, 0);
        detection.Children.Add(_requireSound);
        detection.Children.Add(Row("Meeting window titles", _titlePatterns));
        detection.Children.Add(Row("Ignore window titles", _excludePatterns));
        detection.Children.Add(Row("Teams process names", _processNames));
        detection.Children.Add(Row("Apps using the microphone", _micApps));
        detection.Children.Add(Hint("Separate several entries with commas. * matches anything."));
        detection.Children.Add(Row("Meeting ends after (seconds)", _grace));
        detection.Children.Add(Row("Check every (seconds)", _poll));

        var obs = Page();
        obs.Children.Add(Row("WebSocket password", _password));
        obs.Children.Add(Row("WebSocket host", _host));
        obs.Children.Add(Row("WebSocket port", _port));
        obs.Children.Add(_launchObs);
        obs.Children.Add(Row("OBS program", obsPathRow));
        obs.Children.Add(Row("Wait for OBS to start (seconds)", _obsWait));

        var browseFolder = new Button { Content = "Browse…", Margin = new Thickness(8, 0, 0, 0) };
        browseFolder.Click += async (_, _) => await BrowseForFolder();
        var folderRow = new DockPanel();
        DockPanel.SetDock(browseFolder, Dock.Right);
        folderRow.Children.Add(browseFolder);
        folderRow.Children.Add(_recFolder);

        var recordings = Page();
        recordings.Children.Add(_organize);
        recordings.Children.Add(Row("Save recordings in", folderRow));
        recordings.Children.Add(_groupByMeeting);
        recordings.Children.Add(Row("File name", _fileName));
        recordings.Children.Add(Hint("{meeting} = meeting or caller name, {date} = 2026-10-07, {time} = 14-30"));
        _example.Margin = new Thickness(0, 6, 0, 0);
        recordings.Children.Add(_example);
        foreach (var box in new[] { _recFolder, _fileName }) box.TextChanged += (_, _) => UpdateExample();
        foreach (var box in new[] { _organize, _groupByMeeting }) box.IsCheckedChanged += (_, _) => UpdateExample();

        var tabs = new TabControl { Margin = new Thickness(12, 4, 12, 0) };
        tabs.Items.Add(Tab("General", general));
        tabs.Items.Add(Tab("OBS", obs));
        tabs.Items.Add(Tab("Recordings", recordings));
        tabs.Items.Add(Tab("Detection", detection));

        var status = Page();
        status.Children.Add(new TextBlock
        {
            Text = "What the app sees right now (updates every 2 seconds). Start a call to check that it's detected.",
            TextWrapping = TextWrapping.Wrap, Opacity = 0.8,
        });
        status.Children.Add(_detectedSummary);
        status.Children.Add(_detectedDetails);
        status.Children.Add(new TextBlock
        {
            Text = $"Version {typeof(SettingsWindow).Assembly.GetName().Version?.ToString(3)}",
            FontSize = 12, Opacity = 0.6, Margin = new Thickness(0, 8, 0, 0),
        });
        tabs.Items.Add(Tab("Status", status));

        _refresh.Tick += async (_, _) => await RefreshDetection();
        Opened += async (_, _) => { _refresh.Start(); await RefreshDetection(); };
        Closed += (_, _) => _refresh.Stop();

        var save = new Button { Content = "Save", IsDefault = true, Classes = { "accent" } };
        var cancel = new Button { Content = "Cancel", IsCancel = true };
        save.Click += (_, _) => Save();
        cancel.Click += (_, _) => Close();
        _testObs.Click += async (_, _) => await TestObs();
        _testToast.Click += async (_, _) => await TestNotification();

        var buttons = new DockPanel { LastChildFill = false };
        var left = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        left.Children.Add(_testObs);
        left.Children.Add(_testToast);
        var right = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        right.Children.Add(cancel);
        right.Children.Add(save);
        DockPanel.SetDock(left, Dock.Left);
        DockPanel.SetDock(right, Dock.Right);
        buttons.Children.Add(left);
        buttons.Children.Add(right);

        var footer = new StackPanel { Margin = new Thickness(20, 8, 20, 16) };
        footer.Children.Add(_status);
        footer.Children.Add(buttons);

        var root = new DockPanel();
        DockPanel.SetDock(footer, Dock.Bottom);
        root.Children.Add(footer);
        root.Children.Add(tabs);
        Content = root;

        LoadForm();
    }

    private static NumericUpDown Number(int min, int max) => new()
    {
        Minimum = min, Maximum = max, Increment = 1, FormatString = "0", MinWidth = 140,
        HorizontalAlignment = HorizontalAlignment.Left,
    };

    private static StackPanel Page() => new() { Spacing = 6, Margin = new Thickness(8, 12, 8, 8) };

    private static TabItem Tab(string header, Control content) => new()
    {
        Header = header, FontSize = 15, Content = new ScrollViewer { Content = content },
    };

    private static TextBox Text(string watermark) => new() { Watermark = watermark };

    private static TextBlock Hint(string text) => new()
    {
        Text = text, FontSize = 12, Opacity = 0.7, Margin = new Thickness(220, -2, 0, 4), TextWrapping = TextWrapping.Wrap,
    };

    private static Grid Row(string label, Control control)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("220,*") };
        var text = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap };
        Grid.SetColumn(control, 1);
        grid.Children.Add(text);
        grid.Children.Add(control);
        return grid;
    }

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
            _example.Text = "Recordings keep the name and folder OBS gives them.";
            return;
        }
        var folder = RecordingOrganizer.TargetFolder(cfg, "Weekly Sync", "<OBS recordings folder>");
        var file = RecordingOrganizer.FileStem(cfg, "Weekly Sync", new DateTime(2026, 10, 7, 14, 30, 0)) + ".mkv";
        _example.Text = $"Example: {Path.Combine(folder, file)}";
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
            Close();
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
        if (error) _status.Foreground = Brushes.IndianRed;
        else _status.ClearValue(TextBlock.ForegroundProperty);
    }
}
