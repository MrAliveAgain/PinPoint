using PinPoint.Core.Geometry;

namespace PinPoint.Core.Tests;

public class QuickSlotLayoutTests
{
    [Fact]
    public void Diamond_is_four_tiles_around_an_empty_centre_without_overlap()
    {
        var offsets = QuickSlotLayout.Offsets(QuickSlotArrangement.Diamond, 4, tile: 40, gap: 6);
        Assert.Equal([(46, 0), (92, 46), (46, 92), (0, 46)], offsets);
        var rects = offsets.Select(o => new PixelRect(o.X, o.Y, 40, 40)).ToList();
        for (int i = 0; i < rects.Count; i++)
            for (int j = i + 1; j < rects.Count; j++)
                Assert.True(rects[i].Intersect(rects[j]).IsEmpty);
        Assert.Equal((132, 132), QuickSlotLayout.Size(QuickSlotArrangement.Diamond, 4, 40, 6));
    }

    [Fact]
    public void Rows_and_columns_hold_one_to_eight()
    {
        Assert.Equal(4, QuickSlotLayout.SlotCount(QuickSlotArrangement.Diamond, 7));
        Assert.Equal(8, QuickSlotLayout.SlotCount(QuickSlotArrangement.Row, 20));
        Assert.Equal(1, QuickSlotLayout.SlotCount(QuickSlotArrangement.Column, 0));
        Assert.Equal((40 * 3 + 6 * 2, 40), QuickSlotLayout.Size(QuickSlotArrangement.Row, 3, 40, 6));
    }

    [Fact]
    public void Clamp_keeps_the_group_on_screen()
    {
        var work = new PixelRect(0, 0, 1000, 800);
        Assert.Equal((868, 668), QuickSlotLayout.Clamp(990, 790, 132, 132, work));
        Assert.Equal((0, 0), QuickSlotLayout.Clamp(-50, -50, 132, 132, work));
    }
}
