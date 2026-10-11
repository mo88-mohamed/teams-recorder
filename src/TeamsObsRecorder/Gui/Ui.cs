using Avalonia;
using Avalonia.Controls;

using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;

namespace TeamsObsRecorder.Gui;

/// <summary>Shared look: colours for light and dark mode, cards, setting rows and line icons.</summary>
public static class Ui
{
    public static readonly IBrush Red = new SolidColorBrush(Color.Parse("#DC2626"));
    public static readonly IBrush Green = new SolidColorBrush(Color.Parse("#16A34A"));
    public static readonly IBrush Amber = new SolidColorBrush(Color.Parse("#D97706"));
    public static readonly IBrush Grey = new SolidColorBrush(Color.Parse("#8A8A8A"));

    public static readonly FontFamily Mono = new("Cascadia Mono, Consolas, DejaVu Sans Mono, monospace");

    // 24x24 line icons.
    public const string IconHome = "M3 3h7v9H3z M14 3h7v5h-7z M14 12h7v9h-7z M3 16h7v5H3z";
    public const string IconGeneral = "M4 7h9 M17 7h3 M15 5v4 M4 17h3 M11 17h9 M9 15v4";
    public const string IconObs = "M3 7h12v10H3z M15 11l6-4v10l-6-4z";
    public const string IconFolder = "M3 6h6l2 2h10v11H3z";
    public const string IconDetect = "M2 12C5 6 19 6 22 12C19 18 5 18 2 12z M9 12A3 3 0 1 0 15 12A3 3 0 1 0 9 12z";

    /// <summary>Theme colours, switched automatically with the OS light/dark setting.</summary>
    public static void AddThemeResources(Application app)
    {
        app.Resources.ThemeDictionaries[ThemeVariant.Light] = new ResourceDictionary
        {
            ["PageBrush"] = Brush("#F3F3F3"),
            ["CardBrush"] = Brush("#FFFFFF"),
            ["CardBorderBrush"] = Brush("#E5E5E5"),
            ["NavSelectedBrush"] = Brush("#E6E6E6"),
            ["IconBrush"] = Brush("#1F1F1F"),
            ["IndicatorBrush"] = Brush("#F2FFFFFF"),
        };
        app.Resources.ThemeDictionaries[ThemeVariant.Dark] = new ResourceDictionary
        {
            ["PageBrush"] = Brush("#202020"),
            ["CardBrush"] = Brush("#2B2B2B"),
            ["CardBorderBrush"] = Brush("#1C1C1C"),
            ["NavSelectedBrush"] = Brush("#2D2D2D"),
            ["IconBrush"] = Brush("#F0F0F0"),
            ["IndicatorBrush"] = Brush("#F2272727"),
        };
    }

    private static SolidColorBrush Brush(string hex) => new(Color.Parse(hex));

    /// <summary>Binds a property to a theme resource so it follows light/dark mode.</summary>
    public static T Themed<T>(this T control, AvaloniaProperty property, string key) where T : Control
    {
        control.Bind(property, control.GetResourceObservable(key));
        return control;
    }

    public static Control Icon(string data, double size = 18)
    {
        var path = new Avalonia.Controls.Shapes.Path
        {
            Data = Geometry.Parse(data), StrokeThickness = 1.7,
            StrokeLineCap = PenLineCap.Round, StrokeJoin = PenLineJoin.Round,
        }.Themed(Avalonia.Controls.Shapes.Shape.StrokeProperty, "IconBrush");
        return new Viewbox { Width = size, Height = size, Child = new Canvas { Width = 24, Height = 24, Children = { path } } };
    }

    public static Border Card(Control content, Thickness? padding = null) => new Border
    {
        CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1),
        Padding = padding ?? new Thickness(18, 14),
        Child = content,
    }.Themed(Border.BackgroundProperty, "CardBrush").Themed(Border.BorderBrushProperty, "CardBorderBrush");

    public static TextBlock Section(string text) => new()
    {
        Text = text, FontWeight = FontWeight.SemiBold, FontSize = 14, Margin = new Thickness(2, 18, 0, 6),
    };

    public static TextBlock Caption(string text) => new()
    {
        Text = text, FontSize = 12, Opacity = 0.7, TextWrapping = TextWrapping.Wrap,
    };

    private static StackPanel Labels(string title, string? description)
    {
        var labels = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        labels.Children.Add(new TextBlock { Text = title, FontSize = 14, TextWrapping = TextWrapping.Wrap });
        if (description is not null) labels.Children.Add(Caption(description));
        return labels;
    }

    /// <summary>A Windows 11 style setting row: title and description on the left, control on the right.</summary>
    public static Border Setting(string title, string? description, Control control)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        var labels = Labels(title, description);
        labels.Margin = new Thickness(0, 0, 16, 0);
        control.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(control, 1);
        grid.Children.Add(labels);
        grid.Children.Add(control);
        return Card(grid);
    }

    /// <summary>A setting row whose control (e.g. a text box) gets the full width below the title.</summary>
    public static Border WideSetting(string title, string? description, Control control, params Control[] extra)
    {
        var stack = new StackPanel { Spacing = 8 };
        stack.Children.Add(Labels(title, description));
        stack.Children.Add(control);
        foreach (var c in extra) stack.Children.Add(c);
        return Card(stack);
    }

    /// <summary>Several cards stacked closely, like a settings group.</summary>
    public static StackPanel Group(params Control[] cards)
    {
        var stack = new StackPanel { Spacing = 4 };
        foreach (var c in cards) stack.Children.Add(c);
        return stack;
    }

    public static ToggleSwitch Toggle() => new() { OnContent = "On", OffContent = "Off", MinWidth = 0 };

    public static string Clock(TimeSpan t) =>
        t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{t.Minutes:00}:{t.Seconds:00}";
}
