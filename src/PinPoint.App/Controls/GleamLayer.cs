using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using PinPoint.Core.Settings;
using Windows.Foundation;
using Windows.UI;

namespace PinPoint.Controls;

/// <summary>Static, input-transparent light catches. Never animate or sample the desktop.</summary>
public sealed class GleamLayer : Grid
{
    private bool _enabled = true;
    private (bool Enabled, GleamStyle Style, int Intensity, ElementTheme Theme, double Radius,
        double Strength, bool EdgeOnly, AppearanceStyle Appearance, Color Accent)? _last;

    public GleamLayer()
    {
        IsHitTestVisible = false;
        Loaded += (_, _) =>
        {
            AppHost.Current.Settings.Changed += OnSettingsChanged;
            Rebuild();
        };
        Unloaded += (_, _) => AppHost.Current.Settings.Changed -= OnSettingsChanged;
        ActualThemeChanged += (_, _) => Rebuild();
    }

    public double Radius { get; set; } = 8;
    public double Strength { get; set; } = 1;
    /// <summary>Snips get a narrow frame only: never colour the captured content.</summary>
    public bool EdgeOnly { get; set; }
    public bool Enabled
    {
        get => _enabled;
        set { if (_enabled == value) return; _enabled = value; Rebuild(); }
    }

    private void OnSettingsChanged(object? sender, EventArgs e) => DispatcherQueue.TryEnqueue(Rebuild);

    private void Rebuild()
    {
        var settings = AppHost.Current.Settings.Current;
        var accent = Application.Current.Resources.TryGetValue("SystemAccentColor", out var value)
            && value is Color color ? color : Color.FromArgb(255, 90, 180, 255);
        var state = (_enabled, settings.Gleam, settings.GleamIntensity, ActualTheme, Radius,
            Strength, EdgeOnly, settings.Appearance, accent);
        if (_last == state) return; // Unrelated settings must not allocate new brushes.
        _last = state;
        Children.Clear();
        if (!_enabled || settings.Gleam == GleamStyle.Off || settings.Appearance != AppearanceStyle.Glass)
        {
            Visibility = Visibility.Collapsed;
            return;
        }
        Visibility = Visibility.Visible;
        double k = (settings.GleamIntensity switch { <= 1 => 0.6, 2 => 1.0, _ => 1.4 }) * Strength;
        var (lead, trail) = settings.Gleam switch
        {
            GleamStyle.Sky => (Rgb(119, 198, 255), Rgb(190, 160, 255)),
            GleamStyle.Sunlight => (Rgb(255, 222, 155), Rgb(255, 154, 91)),
            GleamStyle.Ember => (Rgb(255, 175, 98), Rgb(231, 82, 91)),
            GleamStyle.Forest => (Rgb(173, 224, 148), Rgb(66, 187, 166)),
            GleamStyle.Prism => (Rgb(152, 220, 255), Rgb(205, 150, 255)),
            _ => (accent, accent),
        };
        bool dark = ActualTheme != ElementTheme.Light;
        if (!EdgeOnly)
        {
            // Two localized light pools, with an unlit centre. No full-surface coloured veil.
            Children.Add(new Border { CornerRadius = new(Radius),
                Background = Pool(lead, new(0.12, 0), 0.85, 0.75, 0.15 * k) });
            Children.Add(new Border { CornerRadius = new(Radius),
                Background = Pool(trail, new(1, 0.95), 0.6, 0.6, 0.07 * k) });
        }
        // A smoked inner bevel separates screenshots from similarly coloured windows behind them.
        if (EdgeOnly)
            Children.Add(new Border { CornerRadius = new(Radius), BorderThickness = new(3),
                BorderBrush = new SolidColorBrush(WithAlpha(Colors.Black, 0.32)) });

        Brush edge = settings.Gleam == GleamStyle.Prism
            ? Gradient((0, WithAlpha(Colors.White, 0.40 * k)),
                (0.22, WithAlpha(lead, 0.28 * k)),
                (0.42, WithAlpha(lead, 0.05 * k)),
                (0.64, WithAlpha(Rgb(155, 157, 255), 0.14 * k)),
                (0.76, WithAlpha(Rgb(119, 219, 211), 0.28 * k)),
                (0.86, WithAlpha(Rgb(247, 218, 151), 0.30 * k)),
                (0.94, WithAlpha(Rgb(244, 153, 180), 0.26 * k)),
                (1, WithAlpha(trail, 0.12 * k)))
            : Gradient((0, WithAlpha(dark ? Colors.White : lead, 0.38 * k)),
                (0.24, WithAlpha(lead, 0.32 * k)),
                (0.52, WithAlpha(lead, 0.05 * k)),
                (0.78, WithAlpha(trail, 0.08 * k)),
                (1, WithAlpha(trail, 0.25 * k)));
        Children.Add(new Border { CornerRadius = new(Radius),
            BorderThickness = new(EdgeOnly ? 2 : 1), BorderBrush = edge });
    }

    private static RadialGradientBrush Pool(Color color, Point centre, double x, double y, double alpha)
    {
        var brush = new RadialGradientBrush { Center = centre, GradientOrigin = centre, RadiusX = x, RadiusY = y };
        brush.GradientStops.Add(new() { Offset = 0, Color = WithAlpha(color, alpha) });
        brush.GradientStops.Add(new() { Offset = 0.35, Color = WithAlpha(color, alpha * 0.35) });
        brush.GradientStops.Add(new() { Offset = 1, Color = WithAlpha(color, 0) });
        return brush;
    }

    private static LinearGradientBrush Gradient(params (double Offset, Color Color)[] stops)
    {
        var brush = new LinearGradientBrush { StartPoint = new(0, 0), EndPoint = new(1, 1) };
        foreach (var (offset, color) in stops) brush.GradientStops.Add(new() { Offset = offset, Color = color });
        return brush;
    }

    private static Color Rgb(byte r, byte g, byte b) => Color.FromArgb(255, r, g, b);
    private static Color WithAlpha(Color c, double a) =>
        Color.FromArgb((byte)Math.Round(Math.Clamp(a, 0, 1) * 255), c.R, c.G, c.B);
}
