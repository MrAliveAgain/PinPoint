namespace PinPoint.Core.Geometry;

/// <summary>
/// Where pins slide to for "peek": out past the nearest side of their monitor, leaving a thin sliver showing,
/// so whatever was under them can be used, then back. Never onto a neighbouring monitor (that would change
/// the window's DPI and size): if that side has a monitor next to it, the other side, then top or bottom.
/// </summary>
public static class PeekLayout
{
    public static PixelRect Target(PixelRect window, PixelRect monitor, IReadOnlyCollection<PixelRect> monitors, int sliver)
    {
        int centerX = window.X + window.Width / 2, centerY = window.Y + window.Height / 2;
        bool leftFirst = centerX < monitor.X + monitor.Width / 2;
        bool topFirst = centerY < monitor.Y + monitor.Height / 2;

        var left = window with { X = monitor.X - window.Width + sliver };
        var right = window with { X = monitor.Right - sliver };
        var top = window with { Y = monitor.Y - window.Height + sliver };
        var bottom = window with { Y = monitor.Bottom - sliver };

        PixelRect[] order = leftFirst
            ? [left, right, topFirst ? top : bottom, topFirst ? bottom : top]
            : [right, left, topFirst ? top : bottom, topFirst ? bottom : top];

        foreach (var spot in order)
        {
            bool onAnotherMonitor = monitors.Any(m => m != monitor && !spot.Intersect(m).IsEmpty);
            if (!onAnotherMonitor) return spot;
        }

        return order[0]; // boxed in on every side: rare; the nearest side anyway
    }

    /// <summary>Ease-out cubic: fast start, gentle landing (feels snappy without a hard stop).</summary>
    public static double EaseOut(double t)
    {
        t = Math.Clamp(t, 0, 1);
        double u = 1 - t;
        return 1 - u * u * u;
    }

    public static PixelRect Lerp(PixelRect start, PixelRect end, double t) => start with
    {
        X = (int)Math.Round(start.X + (end.X - start.X) * t),
        Y = (int)Math.Round(start.Y + (end.Y - start.Y) * t),
    };
}
