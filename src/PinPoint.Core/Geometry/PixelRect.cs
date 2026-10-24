using PinPoint.Core.Interop;

namespace PinPoint.Core.Geometry;

/// <summary>An integer rectangle in physical (screen) pixels.</summary>
public readonly record struct PixelRect(int X, int Y, int Width, int Height)
{
    public static PixelRect Empty => default;

    public int Right => X + Width;

    public int Bottom => Y + Height;

    public bool IsEmpty => Width <= 0 || Height <= 0;

    public long Area => IsEmpty ? 0 : (long)Width * Height;

    public static PixelRect FromEdges(int left, int top, int right, int bottom) =>
        new(left, top, Math.Max(0, right - left), Math.Max(0, bottom - top));

    /// <summary>Rectangle spanned by two arbitrary corner points (any drag direction).</summary>
    public static PixelRect FromPoints(int x1, int y1, int x2, int y2) =>
        FromEdges(Math.Min(x1, x2), Math.Min(y1, y2), Math.Max(x1, x2), Math.Max(y1, y2));

    public static PixelRect FromRect(in Win32.RECT r) => FromEdges(r.Left, r.Top, r.Right, r.Bottom);

    public bool Contains(int x, int y) => x >= X && x < Right && y >= Y && y < Bottom;

    public PixelRect Intersect(PixelRect other)
    {
        int left = Math.Max(X, other.X);
        int top = Math.Max(Y, other.Y);
        int right = Math.Min(Right, other.Right);
        int bottom = Math.Min(Bottom, other.Bottom);
        return right > left && bottom > top ? FromEdges(left, top, right, bottom) : Empty;
    }

    public PixelRect Offset(int dx, int dy) => this with { X = X + dx, Y = Y + dy };

    public override string ToString() => $"{Width}×{Height} @ {X},{Y}";
}
