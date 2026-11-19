using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using PinPoint.Controls;
using PinPoint.Core.Settings;
using Windows.UI;

namespace PinPoint.Helpers;

/// <summary>
/// Tints the primary Liquid Glass drop (Snip &amp; pin) with the chosen Gleam light, so it belongs to the same
/// scene as the glass around it: sky blue, sunlit gold, ember, forest teal, prism periwinkle, or the Windows accent.
/// It recolours the shared theme brushes in place, so every glass button updates live. On the light theme the
/// colour is deepened so white labels stay readable.
/// </summary>
public static class GlassTint
{
    public static void Apply(AppSettings settings)
    {
        var resources = Application.Current.Resources;
        Color accent = resources.TryGetValue("SystemAccentColor", out var a) && a is Color c ? c : Color.FromArgb(255, 0, 120, 212);
        var (lead, trail) = GleamLayer.PresetColors(settings.Gleam, accent);
        // Mostly the incoming light, with a little of where it's heading: richer than either alone.
        var tint = Mix(lead, trail, 0.35);
        if (settings.Gleam == GleamStyle.Off) tint = accent;

        foreach (var dictionary in resources.MergedDictionaries.Prepend(resources))
        {
            foreach (var (theme, factor) in new[] { ("Default", 1.0), ("Light", 0.62) })
            {
                if (dictionary.ThemeDictionaries.TryGetValue(theme, out var t) && t is ResourceDictionary themeDict &&
                    themeDict.TryGetValue("GlassTintAccent", out var b) && b is SolidColorBrush brush)
                {
                    brush.Color = Darken(tint, factor);
                }
            }
        }
    }

    private static Color Mix(Color x, Color y, double t) => Color.FromArgb(255,
        (byte)Math.Round(x.R + (y.R - x.R) * t), (byte)Math.Round(x.G + (y.G - x.G) * t), (byte)Math.Round(x.B + (y.B - x.B) * t));

    private static Color Darken(Color c, double factor) =>
        Color.FromArgb(255, (byte)(c.R * factor), (byte)(c.G * factor), (byte)(c.B * factor));
}
