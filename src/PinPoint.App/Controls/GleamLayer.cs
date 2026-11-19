using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using PinPoint.Core.Settings;
using Windows.Foundation;
using Windows.UI;

namespace PinPoint.Controls;

/// <summary>
/// Light on glass. Three stacked, click-through layers that make an acrylic surface read as a physical pane:
/// <list type="bullet">
/// <item>a <b>rim</b>: a 1px edge that's bright where the light comes from (top-left) and tinted where it leaves,</item>
/// <item>a <b>sheen</b>: a soft white falloff across the top, like a reflection,</item>
/// <item>the <b>light</b> itself, a diagonal wash in the chosen colour: accent shades, a prism's spectral band,
/// sky (cool blue into warm peach) or sunlight (gold).</item>
/// </list>
/// Follows Settings > Gleam live. The host turns it on only while the surface is actually glass.
/// </summary>
public sealed class GleamLayer : Grid
{
    private bool _enabled = true;

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

    /// <summary>Corner radius of the rim (match the window's rounded corners).</summary>
    public double Radius { get; set; } = 8;

    /// <summary>Overall strength multiplier for this surface (e.g. lower on the big main window).</summary>
    public double Strength { get; set; } = 1;

    /// <summary>Off while the surface isn't glass (faded pins, Solid appearance).</summary>
    public bool Enabled
    {
        get => _enabled;
        set
        {
            if (_enabled == value) return;
            _enabled = value;
            Rebuild();
        }
    }

    private void OnSettingsChanged(object? sender, EventArgs e) => DispatcherQueue.TryEnqueue(Rebuild);

    private void Rebuild()
    {
        Children.Clear();
        var settings = AppHost.Current.Settings.Current;
        if (!_enabled || settings.Gleam == GleamStyle.Off)
        {
            Visibility = Visibility.Collapsed;
            return;
        }

        Visibility = Visibility.Visible;
        double a = settings.GleamIntensity switch { <= 1 => 0.09, 2 => 0.16, _ => 0.26 } * Strength;
        bool dark = ActualTheme != ElementTheme.Light;
        var (lead, trail) = LightColors(settings.Gleam);

        // 1. The light: a diagonal wash (or, for the prism, a thin spectral band).
        Children.Add(new Border { CornerRadius = new CornerRadius(Radius), Background = Light(settings.Gleam, a, dark) });

        // 2. Sheen: a reflection fading down from the top edge.
        Children.Add(new Border
        {
            CornerRadius = new CornerRadius(Radius),
            Background = Gradient(new Point(0.5, 0), new Point(0.5, 0.45),
                (0, WithAlpha(Colors.White, a * (dark ? 0.9 : 1.4))), (1, WithAlpha(Colors.White, 0))),
        });

        // 3. Rim: bright where the light enters (top-left), tinted where it leaves (bottom-right).
        Children.Add(new Border
        {
            CornerRadius = new CornerRadius(Radius),
            BorderThickness = new Thickness(1),
            BorderBrush = Gradient(new Point(0, 0), new Point(1, 1),
                (0, WithAlpha(Colors.White, Math.Min(1, a * 3.2))),
                (0.45, WithAlpha(lead, a * 0.8)),
                (0.7, WithAlpha(Colors.White, 0)),
                (1, WithAlpha(trail, Math.Min(1, a * 2.4)))),
        });
    }

    private static Brush Light(GleamStyle style, double a, bool dark)
    {
        if (style == GleamStyle.Prism)
        {
            // A clean surface with one narrow band of dispersed light across the lower-right, like sun through a prism.
            // Narrow and soft-edged (a light leak, not a stripe), tucked toward the corner; peaks in the middle colours.
            double p = a * (dark ? 1.15 : 0.95);
            return Gradient(new Point(0, 0), new Point(1, 1),
                (0.00, WithAlpha(Colors.White, a * 0.45)),
                (0.45, WithAlpha(Colors.White, 0)),
                (0.69, WithAlpha(Hsv(350, 0.7, 1), 0)),
                (0.72, WithAlpha(Hsv(350, 0.7, 1), p * 0.55)), // red
                (0.745, WithAlpha(Hsv(32, 0.75, 1), p * 0.85)), // orange
                (0.77, WithAlpha(Hsv(55, 0.7, 1), p)),          // yellow
                (0.795, WithAlpha(Hsv(140, 0.55, 1), p)),       // green
                (0.82, WithAlpha(Hsv(205, 0.65, 1), p * 0.85)), // blue
                (0.845, WithAlpha(Hsv(265, 0.55, 1), p * 0.55)), // violet
                (0.88, WithAlpha(Hsv(265, 0.55, 1), 0)));
        }

        var (lead, trail) = LightColors(style);
        return Gradient(new Point(0, 0), new Point(1, 1),
            (0, WithAlpha(lead, a * 1.3)),
            (0.5, WithAlpha(lead, 0)),
            (1, WithAlpha(trail, a * 1.1)));
    }

    /// <summary>The colour light comes in with (top-left) and leaves with (bottom-right).</summary>
    private static (Color Lead, Color Trail) LightColors(GleamStyle style)
    {
        var res = Application.Current.Resources;
        Color accent = res.TryGetValue("SystemAccentColorLight2", out var l) && l is Color light ? light : Color.FromArgb(255, 96, 205, 255);
        Color accentDeep = res.TryGetValue("SystemAccentColor", out var d) && d is Color deep ? deep : Color.FromArgb(255, 0, 120, 212);
        return style switch
        {
            GleamStyle.Sky => (Color.FromArgb(255, 150, 210, 255), Color.FromArgb(255, 255, 196, 150)),
            GleamStyle.Sunlight => (Color.FromArgb(255, 255, 228, 160), Color.FromArgb(255, 255, 170, 90)),
            GleamStyle.Prism => (Color.FromArgb(255, 200, 225, 255), Hsv(265, 0.5, 1)),
            _ => (accent, accentDeep),
        };
    }

    private static LinearGradientBrush Gradient(Point start, Point end, params (double Offset, Color Color)[] stops)
    {
        var brush = new LinearGradientBrush { StartPoint = start, EndPoint = end };
        foreach (var (offset, color) in stops) brush.GradientStops.Add(new GradientStop { Offset = offset, Color = color });
        return brush;
    }

    private static Color WithAlpha(Color c, double alpha) => Color.FromArgb((byte)Math.Round(Math.Clamp(alpha, 0, 1) * 255), c.R, c.G, c.B);

    private static Color Hsv(double h, double s, double v)
    {
        h = ((h % 360) + 360) % 360;
        double c = v * s, x = c * (1 - Math.Abs(h / 60 % 2 - 1)), m = v - c;
        var (r, g, b) = h switch
        {
            < 60 => (c, x, 0d),
            < 120 => (x, c, 0d),
            < 180 => (0d, c, x),
            < 240 => (0d, x, c),
            < 300 => (x, 0d, c),
            _ => (c, 0d, x),
        };
        return Color.FromArgb(255, (byte)((r + m) * 255), (byte)((g + m) * 255), (byte)((b + m) * 255));
    }
}
