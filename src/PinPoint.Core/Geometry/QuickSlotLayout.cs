namespace PinPoint.Core.Geometry;

public enum QuickSlotArrangement
{
    /// <summary>Four slots around an empty centre, like a game's skill slots or a d-pad.</summary>
    Diamond,
    Row,
    Column,
}

/// <summary>Where each quick-slot tile goes, relative to the top-left of the group.</summary>
public static class QuickSlotLayout
{
    public const int MaxSlots = 8;

    /// <summary>The diamond always has four slots; a row or column has 1–8.</summary>
    public static int SlotCount(QuickSlotArrangement arrangement, int wanted) =>
        arrangement == QuickSlotArrangement.Diamond ? 4 : Math.Clamp(wanted, 1, MaxSlots);

    /// <summary>Top-left of every tile (in order: diamond is top, right, bottom, left).</summary>
    public static List<(int X, int Y)> Offsets(QuickSlotArrangement arrangement, int count, int tile, int gap)
    {
        int step = tile + gap;
        return arrangement switch
        {
            QuickSlotArrangement.Diamond => [(step, 0), (2 * step, step), (step, 2 * step), (0, step)],
            QuickSlotArrangement.Row => [.. Enumerable.Range(0, count).Select(i => (i * step, 0))],
            _ => [.. Enumerable.Range(0, count).Select(i => (0, i * step))],
        };
    }

    /// <summary>The whole group's size.</summary>
    public static (int Width, int Height) Size(QuickSlotArrangement arrangement, int count, int tile, int gap)
    {
        var offsets = Offsets(arrangement, count, tile, gap);
        return (offsets.Max(o => o.X) + tile, offsets.Max(o => o.Y) + tile);
    }

    /// <summary>Keeps the group's top-left on screen (whole group inside the work area).</summary>
    public static (int X, int Y) Clamp(int x, int y, int width, int height, PixelRect work) =>
        (Math.Clamp(x, work.X, Math.Max(work.X, work.Right - width)), Math.Clamp(y, work.Y, Math.Max(work.Y, work.Bottom - height)));
}
