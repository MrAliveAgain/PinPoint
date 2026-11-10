using PinPoint.Core.Geometry;
using PinPoint.Core.Notes;
using Xunit;

namespace PinPoint.Core.Tests;

public class NotePlacementTests
{
    private static readonly PixelRect Screen = new(0, 0, 3440, 1400);

    [Fact]
    public void Goes_to_the_right_when_there_is_room()
    {
        var spot = NotePlacement.Beside(new PixelRect(100, 100, 300, 250), Screen, [], gap: 10);
        Assert.Equal(new PixelRect(410, 100, 300, 250), spot);
    }

    [Fact]
    public void Goes_left_at_the_right_edge()
    {
        var spot = NotePlacement.Beside(new PixelRect(3200, 100, 200, 250), Screen, [], gap: 10);
        Assert.Equal(new PixelRect(2990, 100, 200, 250), spot);
    }

    [Fact]
    public void Skips_spots_taken_by_other_notes()
    {
        var source = new PixelRect(500, 100, 300, 250);
        var right = new PixelRect(810, 100, 300, 250);
        var spot = NotePlacement.Beside(source, Screen, [right], gap: 10);
        Assert.Equal(new PixelRect(190, 100, 300, 250), spot); // left instead
    }

    [Fact]
    public void Overlaps_rather_than_going_off_screen_when_every_side_is_taken()
    {
        var source = new PixelRect(500, 500, 300, 250);
        PixelRect[] others =
        [
            new(810, 500, 300, 250), new(190, 500, 300, 250), new(500, 760, 300, 250), new(500, 240, 300, 250),
        ];
        var spot = NotePlacement.Beside(source, Screen, others, gap: 10);
        Assert.Equal(new PixelRect(810, 500, 300, 250), spot);
    }

    [Fact]
    public void Cascades_inside_the_screen_when_nothing_fits_beside_it()
    {
        var huge = new PixelRect(0, 0, 3000, 1300);
        var spot = NotePlacement.Beside(huge, Screen, [], gap: 10);
        Assert.Equal(new PixelRect(30, 30, 3000, 1300), spot);
        Assert.True(spot.Right <= Screen.Right && spot.Bottom <= Screen.Bottom);
    }
}
