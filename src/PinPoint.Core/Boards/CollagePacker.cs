using PinPoint.Core.Geometry;

namespace PinPoint.Core.Boards;

/// <summary>
/// "Pack together" for board pictures: the same pins at the same sizes, but in tidy rows with no empty
/// screen between them, so a board spread across an ultrawide still reads well as one image.
/// Pins keep their reading order (top to bottom, left to right) and rows aim for a roughly 16:9 picture.
/// </summary>
public static class CollagePacker
{
    /// <summary>New positions for <paramref name="items"/>, in the same order, starting at (0, 0).</summary>
    public static IReadOnlyList<PixelRect> Pack(IReadOnlyList<PixelRect> items, int gap)
    {
        if (items.Count == 0) return [];

        double area = items.Sum(r => (double)(r.Width + gap) * (r.Height + gap));
        int rowWidth = Math.Max(items.Max(r => r.Width), (int)Math.Sqrt(area * 16 / 9));

        var packed = new PixelRect[items.Count];
        int x = 0, y = 0, rowHeight = 0;
        foreach (int i in Enumerable.Range(0, items.Count).OrderBy(i => items[i].Y).ThenBy(i => items[i].X))
        {
            var r = items[i];
            if (x > 0 && x + r.Width > rowWidth)
            {
                x = 0;
                y += rowHeight + gap;
                rowHeight = 0;
            }

            packed[i] = new PixelRect(x, y, r.Width, r.Height);
            x += r.Width + gap;
            rowHeight = Math.Max(rowHeight, r.Height);
        }

        return packed;
    }
}
