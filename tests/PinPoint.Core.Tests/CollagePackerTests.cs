using PinPoint.Core.Boards;
using PinPoint.Core.Geometry;

namespace PinPoint.Core.Tests;

public sealed class CollagePackerTests
{
    [Fact]
    public void Nothing_packs_to_nothing() => Assert.Empty(CollagePacker.Pack([], 10));

    [Fact]
    public void Pins_spread_across_a_wide_screen_end_up_close_together_without_overlapping()
    {
        // Four snips at the far corners of a 3440×1440 ultrawide.
        PixelRect[] spread =
        [
            new(3000, 1100, 400, 300),
            new(20, 20, 600, 200),
            new(3100, 30, 300, 500),
            new(40, 1200, 500, 200),
        ];

        var packed = CollagePacker.Pack(spread, 16);

        Assert.Equal(spread.Select(r => (r.Width, r.Height)), packed.Select(r => (r.Width, r.Height)));
        for (int a = 0; a < packed.Count; a++)
        {
            for (int b = a + 1; b < packed.Count; b++) Assert.True(packed[a].Intersect(packed[b]).IsEmpty, $"{a} overlaps {b}");
        }

        int width = packed.Max(r => r.Right), height = packed.Max(r => r.Bottom);
        Assert.True(width < 1600 && height < 1100, $"packed into {width}×{height}");
    }

    [Fact]
    public void Reading_order_is_kept()
    {
        // Same row on screen: left one first. Lower one comes after.
        PixelRect[] items = [new(900, 0, 100, 100), new(0, 0, 100, 100), new(0, 900, 100, 100)];

        var packed = CollagePacker.Pack(items, 10);

        Assert.Equal((0, 0), (packed[1].X, packed[1].Y));
        Assert.True(packed[0].X > packed[1].X || packed[0].Y > packed[1].Y);
        Assert.True(packed[2].Y >= packed[0].Y && (packed[2].Y > packed[0].Y || packed[2].X > packed[0].X));
    }
}
