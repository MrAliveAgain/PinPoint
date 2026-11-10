using PinPoint.Core.Geometry;

namespace PinPoint.Core.Notes;

/// <summary>Where a note opened with a note's + button goes: next to that note.</summary>
public static class NotePlacement
{
    /// <summary>
    /// Places a <paramref name="width"/>×<paramref name="height"/> note beside <paramref name="source"/>, trying
    /// right, left, below, then above (with a small gap, top- or left-aligned with the source). Takes the first
    /// spot that fits in the work area and doesn't cover another note. If none does, it settles for a spot
    /// that fits even if it overlaps, and failing that a small diagonal offset kept inside the work area.
    /// </summary>
    public static PixelRect Beside(PixelRect source, int width, int height, PixelRect workArea,
        IReadOnlyCollection<PixelRect> otherNotes, int gap)
    {
        int w = width, h = height;
        PixelRect[] candidates =
        [
            new(source.Right + gap, source.Y, w, h),
            new(source.X - gap - w, source.Y, w, h),
            new(source.X, source.Bottom + gap, w, h),
            new(source.X, source.Y - gap - h, w, h),
        ];

        var fitting = candidates.Where(c => Fits(c, workArea)).ToList();
        foreach (var spot in fitting)
        {
            if (!otherNotes.Any(n => !spot.Intersect(n).IsEmpty)) return spot;
        }

        if (fitting.Count > 0) return fitting[0];

        // No room beside it (e.g. a big note): cascade, clamped so it stays on screen.
        int offset = Math.Max(gap * 3, 24);
        int x = Math.Clamp(source.X + offset, workArea.X, Math.Max(workArea.X, workArea.Right - w));
        int y = Math.Clamp(source.Y + offset, workArea.Y, Math.Max(workArea.Y, workArea.Bottom - h));
        return new PixelRect(x, y, w, h);
    }

    private static bool Fits(PixelRect r, PixelRect area) =>
        r.X >= area.X && r.Y >= area.Y && r.Right <= area.Right && r.Bottom <= area.Bottom;
}
