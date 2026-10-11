using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

namespace TeamsObsRecorder.Gui;

/// <summary>
/// A small always-on-top bar shown while recording: blinking red dot, elapsed time, meeting
/// name, and Pause / Stop buttons. It can be dragged anywhere and starts at the top centre of the
/// screen for each recording.
/// </summary>
public sealed class RecordingIndicator : Window
{
    private readonly MeetingWatcher _watcher;
    private readonly Ellipse _dot = new() { Width = 10, Height = 10, Fill = Ui.Red, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _rec = new() { Text = "REC", FontWeight = FontWeight.Bold, FontSize = 12, Foreground = Ui.Red, VerticalAlignment = VerticalAlignment.Center, MinWidth = 54 };
    private readonly TextBlock _time = new() { FontFamily = Ui.Mono, FontSize = 14, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _name = new() { FontSize = 12, Opacity = 0.75, MaxWidth = 200, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
    private readonly Button _pause = SmallButton("❚❚", "Pause recording");
    private readonly Button _stop = SmallButton("■", "Stop recording");
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private bool _blink;

    public RecordingIndicator(MeetingWatcher watcher)
    {
        _watcher = watcher;
        Title = "Recording";
        SystemDecorations = SystemDecorations.None;
        Topmost = true;
        ShowInTaskbar = false;
        CanResize = false;
        SizeToContent = SizeToContent.WidthAndHeight;
        Background = Brushes.Transparent;
        TransparencyLevelHint = new[] { WindowTransparencyLevel.Transparent, WindowTransparencyLevel.None };
        WindowStartupLocation = WindowStartupLocation.Manual;

        _stop.Foreground = Ui.Red;
        _pause.Click += async (_, _) => await Run(() =>
        {
            if (_watcher.IsRecordingPaused) _watcher.ResumeRecording(); else _watcher.PauseRecording();
        });
        _stop.Click += async (_, _) => await Run(_watcher.StopRecordingNow);

        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        row.Children.Add(_dot);
        row.Children.Add(_rec);
        row.Children.Add(_time);
        row.Children.Add(_name);
        row.Children.Add(_pause);
        row.Children.Add(_stop);

        var pill = new Border
        {
            Child = row, CornerRadius = new CornerRadius(20), Padding = new Thickness(14, 6, 6, 6),
            BorderThickness = new Thickness(1), BorderBrush = new SolidColorBrush(Color.Parse("#40DC2626")),
        }.Themed(Border.BackgroundProperty, "IndicatorBrush");
        pill.PointerPressed += (_, e) =>
        {
            if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) BeginMoveDrag(e);
        };
        Content = pill;

        _timer.Tick += (_, _) => Refresh();
        Opened += (_, _) =>
        {
            PlaceAtTop();
            Refresh();
            _timer.Start();
        };
        Closed += (_, _) => _timer.Stop();
    }

    private static Button SmallButton(string text, string tip)
    {
        var b = new Button
        {
            Content = text, FontSize = 11, Width = 30, Height = 28, Padding = new Thickness(0),
            HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center,
            CornerRadius = new CornerRadius(14),
        };
        ToolTip.SetTip(b, tip);
        return b;
    }

    private void PlaceAtTop()
    {
        var screen = Screens.Primary ?? Screens.All.FirstOrDefault();
        if (screen is null) return;
        var area = screen.WorkingArea;
        var width = (int)(Bounds.Width * screen.Scaling);
        Position = new PixelPoint(area.X + (area.Width - width) / 2, area.Y + (int)(12 * screen.Scaling));
    }

    private void Refresh()
    {
        var paused = _watcher.IsRecordingPaused;
        _blink = !_blink;
        _dot.Opacity = paused ? 0.35 : _blink ? 1 : 0.25;
        _rec.Text = paused ? "PAUSED" : "REC";
        _rec.Foreground = paused ? Ui.Amber : Ui.Red;
        _time.Text = Ui.Clock(_watcher.RecordingElapsed);
        var name = _watcher.MeetingTitle;
        _name.Text = name;
        _name.IsVisible = name.Length > 0;
        _pause.Content = paused ? "▶" : "❚❚";
        ToolTip.SetTip(_pause, paused ? "Resume recording" : "Pause recording");
    }

    private async Task Run(Action action)
    {
        _pause.IsEnabled = _stop.IsEnabled = false;
        try { await Task.Run(action); }
        catch (Exception ex) { Log.Error("Recording control failed", ex); }
        _pause.IsEnabled = _stop.IsEnabled = true;
        Refresh();
    }
}
