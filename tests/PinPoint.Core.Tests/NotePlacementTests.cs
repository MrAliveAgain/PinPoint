using PinPoint.Core.Geometry;
using PinPoint.Core.Notes;
using Xunit;

namespace PinPoint.Core.Tests;

public class NotePlacementTests
{
    private static readonly PixelRect Screen = new(0, 0, 3440, 1400);

    private static PixelRect Place(PixelRect source, PixelRect[] others, int height = 250) =>
        NotePlacement.Beside(source, source.Width, height, Screen, others, gap: 10);

    [Fact]
    public void Goes_to_the_right_when_there_is_room()
    {
        Assert.Equal(new PixelRect(410, 100, 300, 250), Place(new PixelRect(100, 100, 300, 250), []));
    }

    [Fact]
    public void Uses_the_given_height_not_the_source_height()
    {
        var spot = Place(new PixelRect(100, 100, 300, 900), [], height: 260);
        Assert.Equal(new PixelRect(410, 100, 300, 260), spot);
    }

    [Fact]
    public void Goes_left_at_the_right_edge()
    {
        Assert.Equal(new PixelRect(2990, 100, 200, 250), Place(new PixelRect(3200, 100, 200, 250), []));
    }

    [Fact]
    public void Skips_spots_taken_by_other_notes()
    {
        var source = new PixelRect(500, 100, 300, 250);
        PixelRect[] others = [new(810, 100, 300, 250)];
        Assert.Equal(new PixelRect(190, 100, 300, 250), Place(source, others)); // left instead
    }

    [Fact]
    public void Overlaps_rather_than_going_off_screen_when_every_side_is_taken()
    {
        var source = new PixelRect(500, 500, 300, 250);
        PixelRect[] others =
        [
            new(810, 500, 300, 250), new(190, 500, 300, 250), new(500, 760, 300, 250), new(500, 240, 300, 250),
        ];
        Assert.Equal(new PixelRect(810, 500, 300, 250), Place(source, others));
    }

    [Fact]
    public void Cascades_inside_the_screen_when_nothing_fits_beside_it()
    {
        var spot = Place(new PixelRect(0, 0, 3000, 1300), [], height: 1300);
        Assert.Equal(new PixelRect(30, 30, 3000, 1300), spot);
        Assert.True(spot.Right <= Screen.Right && spot.Bottom <= Screen.Bottom);
    }
}
