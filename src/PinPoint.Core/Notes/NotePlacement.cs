using PinPoint.Core.Geometry;

namespace PinPoint.Core.Notes;

/// <summary>Where a note opened with a note's + button goes: next to that note, same size.</summary>
public static class NotePlacement
{
    /// <summary>
    /// Tries right, left, below, then above <paramref name="source"/> (with a small gap), taking the first
    /// spot that fits in the work area and doesn't cover another note. If none does, it settles for a spot
    /// that fits even if it overlaps, and failing that a small diagonal offset kept inside the work area.
    /// </summary>
    public static PixelRect Beside(PixelRect source, PixelRect workArea, IReadOnlyCollection<PixelRect> otherNotes, int gap)
    {
        int w = source.Width, h = source.Height;
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
