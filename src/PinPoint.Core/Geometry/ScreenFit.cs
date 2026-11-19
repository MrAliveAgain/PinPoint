namespace PinPoint.Core.Geometry;

/// <summary>Keeps pins findable: never so far off-screen that you can't see or grab them.</summary>
public static class ScreenFit
{
    /// <summary>
    /// Moves <paramref name="rect"/> just enough that at least <paramref name="minVisible"/> pixels of it (each
    /// way) are on a monitor (the one it overlaps most, or the nearest). Used after zooming and dragging.
    /// </summary>
    public static PixelRect KeepReachable(PixelRect rect, IReadOnlyCollection<PixelRect> monitors, int minVisible)
    {
        if (monitors.Count == 0) return rect;
        var monitor = Best(rect, monitors);
        int minX = Math.Min(minVisible, rect.Width), minY = Math.Min(minVisible, rect.Height);
        int x = Math.Clamp(rect.X, monitor.X - rect.Width + minX, monitor.Right - minX);
        int y = Math.Clamp(rect.Y, monitor.Y - rect.Height + minY, monitor.Bottom - minY);
        return rect with { X = x, Y = y };
    }

    /// <summary>
    /// For pins coming back (a board opening, undo): if barely any of it is showing, bring the whole thing onto
    /// its monitor, top-left first when it's bigger than the screen. Pins that are reasonably on screen stay put.
    /// </summary>
    public static PixelRect BringOnScreen(PixelRect rect, IReadOnlyCollection<PixelRect> monitors, int minVisible)
    {
        if (monitors.Count == 0) return rect;
        var monitor = Best(rect, monitors);
        var shown = rect.Intersect(monitor);
        if (shown.Width >= Math.Min(minVisible, rect.Width) && shown.Height >= Math.Min(minVisible, rect.Height)) return rect;

        int x = Math.Clamp(rect.X, monitor.X, Math.Max(monitor.X, monitor.Right - rect.Width));
        int y = Math.Clamp(rect.Y, monitor.Y, Math.Max(monitor.Y, monitor.Bottom - rect.Height));
        return rect with { X = x, Y = y };
    }

    /// <summary>The monitor the rect overlaps most; if it overlaps none, the one nearest its centre.</summary>
    private static PixelRect Best(PixelRect rect, IReadOnlyCollection<PixelRect> monitors)
    {
        var overlapping = monitors.MaxBy(m => rect.Intersect(m).Area);
        if (overlapping.Area > 0 && rect.Intersect(overlapping).Area > 0) return overlapping;

        long cx = rect.X + rect.Width / 2, cy = rect.Y + rect.Height / 2;
        return monitors.MinBy(m =>
        {
            long dx = Math.Max(Math.Max(m.X - cx, 0), cx - m.Right);
            long dy = Math.Max(Math.Max(m.Y - cy, 0), cy - m.Bottom);
            return dx * dx + dy * dy;
        });
    }
}
