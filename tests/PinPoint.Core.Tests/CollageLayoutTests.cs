using PinPoint.Core.Boards;
using PinPoint.Core.Geometry;

namespace PinPoint.Core.Tests;

public sealed class CollageLayoutTests
{
    [Fact]
    public void Nothing_to_lay_out_gives_no_layout()
    {
        Assert.Null(CollageLayout.Fit([], 10, 1000, 1000));
        Assert.Null(CollageLayout.Fit([PixelRect.Empty], 10, 1000, 1000));
    }

    [Fact]
    public void Crops_to_the_items_plus_margin_at_full_size()
    {
        // Two items on a second monitor to the left (negative x).
        var layout = CollageLayout.Fit([new PixelRect(-500, 100, 200, 100), new PixelRect(-200, 300, 100, 50)], 20, 4000, 4000)!.Value;

        Assert.Equal(1, layout.Scale);
        Assert.Equal((400 + 40, 250 + 40), (layout.Width, layout.Height));
        Assert.Equal((20.0, 20.0, 200.0, 100.0), layout.Map(new PixelRect(-500, 100, 200, 100)));
        Assert.Equal((320.0, 220.0, 100.0, 50.0), layout.Map(new PixelRect(-200, 300, 100, 50)));
    }

    [Fact]
    public void Scales_down_to_fit_but_never_up()
    {
        var big = CollageLayout.Fit([new PixelRect(0, 0, 3800, 1000)], 100, 400, 400)!.Value;
        Assert.Equal(0.1, big.Scale, 6);
        Assert.Equal((400, 120), (big.Width, big.Height));
        Assert.Equal((10.0, 10.0, 380.0, 100.0), big.Map(new PixelRect(0, 0, 3800, 1000)));

        var small = CollageLayout.Fit([new PixelRect(5, 5, 50, 50)], 0, 400, 400)!.Value;
        Assert.Equal(1, small.Scale);
        Assert.Equal((50, 50), (small.Width, small.Height));
    }
}
