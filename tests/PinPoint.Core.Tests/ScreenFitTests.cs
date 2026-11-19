using PinPoint.Core.Geometry;

namespace PinPoint.Core.Tests;

public class ScreenFitTests
{
    private static readonly PixelRect[] Screen = [new(0, 0, 3440, 1440)];

    [Fact]
    public void A_pin_flung_far_off_screen_comes_back_onto_it()
    {
        // The real case: a full-screen snip zoomed past what Windows allows ended up here.
        var lost = new PixelRect(-6553, -6859, 3444, 1452);
        var back = ScreenFit.BringOnScreen(lost, Screen, minVisible: 120);
        Assert.Equal(new PixelRect(0, 0, 3444, 1452), back);
    }

    [Fact]
    public void Pins_reasonably_on_screen_are_left_alone()
    {
        var partly = new PixelRect(-300, 616, 1415, 936);
        Assert.Equal(partly, ScreenFit.BringOnScreen(partly, Screen, minVisible: 120));
        Assert.Equal(partly, ScreenFit.KeepReachable(partly, Screen, minVisible: 120));
    }

    [Fact]
    public void Keep_reachable_leaves_a_grabbable_piece_showing()
    {
        var offLeft = new PixelRect(-5000, 200, 1000, 500);
        Assert.Equal(new PixelRect(-880, 200, 1000, 500), ScreenFit.KeepReachable(offLeft, Screen, minVisible: 120));

        var offBottom = new PixelRect(500, 3000, 400, 300);
        Assert.Equal(new PixelRect(500, 1320, 400, 300), ScreenFit.KeepReachable(offBottom, Screen, minVisible: 120));
    }

    [Fact]
    public void Uses_the_nearest_monitor_when_it_overlaps_none()
    {
        PixelRect[] two = [new(0, 0, 1920, 1080), new(1920, 0, 2560, 1440)];
        var farRight = new PixelRect(9000, 100, 400, 300);
        var spot = ScreenFit.BringOnScreen(farRight, two, minVisible: 120);
        Assert.Equal(new PixelRect(4080, 100, 400, 300), spot);
    }
}
