using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using PinPoint.Core.Settings;
using Windows.Foundation;
using Windows.UI;

namespace PinPoint.Controls;

/// <summary>
/// A button drawn as a drop of water resting on the window's glass, lit by the same global light as the rest of
/// the container (GleamLayer: the preset's lead light enters top-left, its trail light leaves bottom-right).
/// <list type="bullet">
/// <item>Body: clear. The window's acrylic is the transparency; the drop adds no fill of its own.</item>
/// <item>Meniscus: a thin dark outline, because a drop bends its darker surroundings into its edge. This is what
/// seats it on the surface instead of floating above it.</item>
/// <item>Specular: a small bright point near the top-left, where the global light strikes.</item>
/// <item>Caustic: the light the drop focuses, pooled inside on the far side (bottom-right), in the trail colour.</item>
/// <item>Rim: the container's diagonal light, entering bright top-left, gone mid-way, a glint bottom-right.</item>
/// </list>
/// The primary drop carries a little of the lead light in its body (light it has gathered), never a paint fill.
/// Static brushes, rebuilt only when the theme or Gleam settings change.
/// </summary>
public sealed class GlassDropLayer : Grid
{
    /// <summary>Set on a Button (from its style) to make it the primary drop, which holds a little of the lead light.</summary>
    public static readonly DependencyProperty PrimaryProperty = DependencyProperty.RegisterAttached(
        "Primary", typeof(bool), typeof(GlassDropLayer), new PropertyMetadata(false));
    public static bool GetPrimary(DependencyObject o) => (bool)o.GetValue(PrimaryProperty);
    public static void SetPrimary(DependencyObject o, bool value) => o.SetValue(PrimaryProperty, value);

    private static readonly Windows.UI.ViewManagement.AccessibilitySettings Accessibility = new();

    public static readonly DependencyProperty RadiusProperty = DependencyProperty.Register(
        nameof(Radius), typeof(CornerRadius), typeof(GlassDropLayer), new PropertyMetadata(new CornerRadius(18), (d, _) => ((GlassDropLayer)d).Rebuild()));

    public GlassDropLayer()
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


    public CornerRadius Radius
    {
        get => (CornerRadius)GetValue(RadiusProperty);
        set => SetValue(RadiusProperty, value);
    }

    private (GleamStyle, int, ElementTheme, bool, CornerRadius, AppearanceStyle, bool)? _last;

    private void OnSettingsChanged(object? sender, EventArgs e) => DispatcherQueue.TryEnqueue(Rebuild);

    private void Rebuild()
    {
        if (!IsLoaded) return;
        var settings = AppHost.Current.Settings.Current;
        bool primary = FindButton() is { } button && GetPrimary(button);
        var state = (settings.Gleam, settings.GleamIntensity, ActualTheme, primary, Radius, settings.Appearance, Accessibility.HighContrast);
        if (_last == state) return;
        _last = state;
        Children.Clear();
        if (Accessibility.HighContrast) return; // System colours draw the button there.
        bool IsPrimary = primary;

        var accent = Application.Current.Resources.TryGetValue("SystemAccentColor", out var v) && v is Color c ? c : Color.FromArgb(255, 90, 180, 255);
        var (lead, trail) = GleamLayer.PresetColors(settings.Gleam == GleamStyle.Off ? GleamStyle.Accent : settings.Gleam, accent);
        bool dark = ActualTheme != ElementTheme.Light;
        // Same intensity scale as the container's gleam, so drops and surface are lit together.
        double k = settings.GleamIntensity switch { <= 1 => 0.75, 2 => 1.0, _ => 1.25 };
        var r = Radius;

        // Primary: light the drop has gathered, pooled where the light enters. Clear drops hold none.
        if (IsPrimary)
            Children.Add(new Border { CornerRadius = r, Background = Pool(lead, new(0.18, 0.05), 0.95, 1.1, (dark ? 0.13 : 0.18) * k) });

        // Caustic: focused light on the far side, inside the drop.
        Children.Add(new Border { CornerRadius = r, Background = Pool(trail, new(0.82, 1.0), 0.55, 0.75, (IsPrimary ? 0.20 : 0.11) * k) });

        // Specular: the point where the global light strikes.
        Children.Add(new Border { CornerRadius = r, Background = Pool(Colors.White, new(0.16, 0.12), 0.16, 0.42, (dark ? 0.30 : 0.45) * k) });

        // Meniscus: the drop's darker edge, which seats it on the surface.
        Children.Add(new Border
        {
            CornerRadius = r,
            BorderThickness = new(1),
            BorderBrush = new SolidColorBrush(WithAlpha(Colors.Black, dark ? 0.22 : 0.12)),
        });

        // Rim light: the container's diagonal, just inside the meniscus.
        Children.Add(new Border
        {
            Margin = new(1),
            CornerRadius = new(Math.Max(0, r.TopLeft - 1), Math.Max(0, r.TopRight - 1), Math.Max(0, r.BottomRight - 1), Math.Max(0, r.BottomLeft - 1)),
            BorderThickness = new(1),
            BorderBrush = Diagonal(
                (0, WithAlpha(dark ? Colors.White : lead, 0.42 * k)),
                (0.22, WithAlpha(lead, 0.26 * k)),
                (0.5, WithAlpha(lead, 0.04 * k)),
                (0.78, WithAlpha(trail, 0.08 * k)),
                (1, WithAlpha(trail, 0.30 * k))),
        });
    }

    private Button? FindButton()
    {
        for (DependencyObject? o = this; o is not null; o = VisualTreeHelper.GetParent(o))
            if (o is Button b) return b;
        return null;
    }

    private static RadialGradientBrush Pool(Color color, Point centre, double rx, double ry, double alpha)
    {
        var brush = new RadialGradientBrush { Center = centre, GradientOrigin = centre, RadiusX = rx, RadiusY = ry };
        brush.GradientStops.Add(new() { Offset = 0, Color = WithAlpha(color, alpha) });
        brush.GradientStops.Add(new() { Offset = 0.45, Color = WithAlpha(color, alpha * 0.3) });
        brush.GradientStops.Add(new() { Offset = 1, Color = WithAlpha(color, 0) });
        return brush;
    }

    private static LinearGradientBrush Diagonal(params (double Offset, Color Color)[] stops)
    {
        var brush = new LinearGradientBrush { StartPoint = new(0, 0), EndPoint = new(1, 1) };
        foreach (var (offset, color) in stops) brush.GradientStops.Add(new() { Offset = offset, Color = color });
        return brush;
    }

    private static Color WithAlpha(Color c, double a) =>
        Color.FromArgb((byte)Math.Round(Math.Clamp(a, 0, 1) * 255), c.R, c.G, c.B);
}
