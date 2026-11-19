using PinPoint.Core.Geometry;

namespace PinPoint.Core.Tests;

public class PeekLayoutTests
{
    private static readonly PixelRect Main = new(0, 0, 3440, 1440);

    [Fact]
    public void Slides_to_the_nearer_side_leaving_a_sliver()
    {
        var leftPin = new PixelRect(200, 300, 400, 300);
        Assert.Equal(new PixelRect(-400 + 12, 300, 400, 300), PeekLayout.Target(leftPin, Main, [Main], sliver: 12));

        var rightPin = new PixelRect(3000, 300, 400, 300);
        Assert.Equal(new PixelRect(3440 - 12, 300, 400, 300), PeekLayout.Target(rightPin, Main, [Main], sliver: 12));
    }

    [Fact]
    public void Avoids_sliding_onto_a_neighbouring_monitor()
    {
        var second = new PixelRect(3440, 0, 1920, 1080);
        var rightPin = new PixelRect(3000, 300, 400, 300);
        var spot = PeekLayout.Target(rightPin, Main, [Main, second], sliver: 12);
        Assert.Equal(-400 + 12, spot.X); // the left side instead
    }

    [Fact]
    public void Ease_out_starts_fast_and_lands_exactly()
    {
        Assert.Equal(0, PeekLayout.EaseOut(0));
        Assert.Equal(1, PeekLayout.EaseOut(1));
        Assert.True(PeekLayout.EaseOut(0.25) > 0.5);
        var from = new PixelRect(0, 0, 10, 10);
        var to = new PixelRect(100, 50, 10, 10);
        Assert.Equal(to, PeekLayout.Lerp(from, to, 1));
    }
}
