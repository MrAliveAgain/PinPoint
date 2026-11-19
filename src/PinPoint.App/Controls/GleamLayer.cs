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
        // k scales everything; white highlights are halved on dark glass (they read twice as strong there).
        double k = settings.GleamIntensity switch { <= 1 => 0.6, 2 => 1.0, _ => 1.5 } * Strength;
        bool dark = ActualTheme != ElementTheme.Light;
        double white = dark ? 0.55 : 1.0;
        var (lead, trail) = LightColors(settings.Gleam);

        // Rules that keep light from turning into dirt (the "dark streak"):
        //  - every fade goes to the SAME colour at zero alpha, never to "transparent" (which is transparent black);
        //  - washes fall off in ONE direction and keep a small floor, so no unlit gap sits between two lit areas.

        // 1. Light: a soft wash of the chosen light from the lit corner (prism: plus a narrow spectral leak).
        Children.Add(new Border { CornerRadius = new CornerRadius(Radius), Background = Light(settings.Gleam, lead, k, dark) });

        // 2. Sheen: white reflection falling from the top edge.
        Children.Add(new Border
        {
            CornerRadius = new CornerRadius(Radius),
            Background = Gradient(new Point(0.5, 0), new Point(0.5, 0.5),
                (0, WithAlpha(Colors.White, 0.10 * k * white)),
                (1, WithAlpha(Colors.White, 0.01 * k * white))),
        });

        // 3. Rim: a white specular edge, strong top-left, near-invisible mid, faint bounce bottom-right.
        Children.Add(new Border
        {
            CornerRadius = new CornerRadius(Radius),
            BorderThickness = new Thickness(1),
            BorderBrush = Gradient(new Point(0, 0), new Point(1, 1),
                (0, WithAlpha(Colors.White, Math.Min(1, 0.60 * k * white))),
                (0.5, WithAlpha(Colors.White, 0.06 * k * white)),
                (1, WithAlpha(Colors.White, 0.22 * k * white))),
        });

        // 4. Dispersion: light splitting at the edge, a faint tint just inside the rim (lead colour in, trail colour out).
        Children.Add(new Border
        {
            Margin = new Thickness(1),
            CornerRadius = new CornerRadius(Math.Max(0, Radius - 1)),
            BorderThickness = new Thickness(1),
            BorderBrush = Gradient(new Point(0, 0), new Point(1, 1),
                (0, WithAlpha(lead, 0.30 * k)),
                (0.5, WithAlpha(lead, 0.03 * k)),
                (0.5, WithAlpha(trail, 0.03 * k)),
                (1, WithAlpha(trail, 0.26 * k))),
        });

        // 5. Specular catch: a 1px highlight right along the top, brightest in the middle third.
        Children.Add(new Border
        {
            Height = 1,
            Margin = new Thickness(Radius, 1, Radius, 0),
            VerticalAlignment = VerticalAlignment.Top,
            Background = Gradient(new Point(0, 0.5), new Point(1, 0.5),
                (0, WithAlpha(Colors.White, 0)),
                (0.35, WithAlpha(Colors.White, 0.55 * k * white)),
                (0.65, WithAlpha(Colors.White, 0.55 * k * white)),
                (1, WithAlpha(Colors.White, 0))),
        });
    }

    private static Brush Light(GleamStyle style, Color lead, double k, bool dark)
    {
        double tint = dark ? 0.16 : 0.12;
        if (style == GleamStyle.Prism)
        {
            // A clean pane with one narrow, soft-edged band of dispersed light tucked toward the corner (a light leak).
            // Neighbouring stops share their colour, so nothing between them can go muddy.
            double p = 0.16 * k * (dark ? 1.0 : 0.85);
            return Gradient(new Point(0, 0), new Point(1, 1),
                (0.00, WithAlpha(Colors.White, 0.06 * k)),
                (0.60, WithAlpha(Colors.White, 0.01 * k)),
                (0.69, WithAlpha(Hsv(350, 0.7, 1), 0)),
                (0.72, WithAlpha(Hsv(350, 0.7, 1), p * 0.55)), // red
                (0.745, WithAlpha(Hsv(32, 0.75, 1), p * 0.85)), // orange
                (0.77, WithAlpha(Hsv(55, 0.7, 1), p)),          // yellow
                (0.795, WithAlpha(Hsv(140, 0.55, 1), p)),       // green
                (0.82, WithAlpha(Hsv(205, 0.65, 1), p * 0.85)), // blue
                (0.845, WithAlpha(Hsv(265, 0.55, 1), p * 0.55)), // violet
                (0.88, WithAlpha(Hsv(265, 0.55, 1), 0)));
        }

        // One-directional: lit corner fades to a faint floor of the same colour across the pane.
        return Gradient(new Point(0, 0), new Point(1, 1),
            (0, WithAlpha(lead, tint * k)),
            (0.6, WithAlpha(lead, tint * k * 0.18)),
            (1, WithAlpha(lead, tint * k * 0.10)));
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
