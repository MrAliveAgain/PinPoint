using PinPoint.Core.Geometry;

namespace PinPoint.Core.Boards;

/// <summary>
/// Where things go on a board image: the on-screen layout cropped to what's on it plus a margin, and
/// scaled down (never up) to fit a size limit. Screen point (x, y) lands at ((x + OffsetX) * Scale, (y + OffsetY) * Scale).
/// </summary>
public readonly record struct CollageLayout(int Width, int Height, double Scale, int OffsetX, int OffsetY)
{
    /// <summary>The layout for these items, or null if there are none.</summary>
    public static CollageLayout? Fit(IEnumerable<PixelRect> items, int margin, int maxWidth, int maxHeight)
    {
        int left = int.MaxValue, top = int.MaxValue, right = int.MinValue, bottom = int.MinValue;
        foreach (var r in items)
        {
            if (r.IsEmpty) continue;
            left = Math.Min(left, r.X);
            top = Math.Min(top, r.Y);
            right = Math.Max(right, r.Right);
            bottom = Math.Max(bottom, r.Bottom);
        }

        if (left == int.MaxValue) return null;

        double width = right - left + 2.0 * margin;
        double height = bottom - top + 2.0 * margin;
        double scale = Math.Min(1, Math.Min(maxWidth / width, maxHeight / height));

        return new CollageLayout(
            Math.Max(1, (int)Math.Ceiling(width * scale)),
            Math.Max(1, (int)Math.Ceiling(height * scale)),
            scale,
            margin - left,
            margin - top);
    }

    /// <summary>A screen rectangle in image coordinates.</summary>
    public (double X, double Y, double Width, double Height) Map(PixelRect r) =>
        ((r.X + OffsetX) * Scale, (r.Y + OffsetY) * Scale, r.Width * Scale, r.Height * Scale);
}
