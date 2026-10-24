using PinPoint.Core.Geometry;

namespace PinPoint.Core.Capture;

/// <summary>An opaque BGRA32 bitmap plus where on the virtual screen it came from.</summary>
public sealed class CapturedFrame
{
    public CapturedFrame(PixelRect bounds, byte[] pixels)
    {
        if (bounds.IsEmpty) throw new ArgumentException("Frame bounds must not be empty.", nameof(bounds));
        if (pixels.Length != bounds.Width * bounds.Height * 4)
            throw new ArgumentException("Pixel buffer size does not match bounds.", nameof(pixels));

        Bounds = bounds;
        Pixels = pixels;
    }

    /// <summary>Location in virtual-screen pixels (can be negative on multi-monitor setups).</summary>
    public PixelRect Bounds { get; }

    public int Width => Bounds.Width;

    public int Height => Bounds.Height;

    public int Stride => Bounds.Width * 4;

    /// <summary>Top-down BGRA32, alpha always 255.</summary>
    public byte[] Pixels { get; }

    /// <summary>Copies out the part of this frame covered by <paramref name="screenRect"/>.</summary>
    public CapturedFrame Crop(PixelRect screenRect)
    {
        var area = screenRect.Intersect(Bounds);
        if (area.IsEmpty) throw new ArgumentOutOfRangeException(nameof(screenRect), "Crop area is outside the frame.");

        int rowBytes = area.Width * 4;
        var pixels = GC.AllocateUninitializedArray<byte>(rowBytes * area.Height);
        int srcX = (area.X - Bounds.X) * 4;
        int srcY = area.Y - Bounds.Y;

        var source = Pixels.AsSpan();
        var target = pixels.AsSpan();
        for (int row = 0; row < area.Height; row++)
        {
            source.Slice((srcY + row) * Stride + srcX, rowBytes).CopyTo(target.Slice(row * rowBytes, rowBytes));
        }

        return new CapturedFrame(area, pixels);
    }
}
