namespace PinPoint.Core.Drawing;

public static class StrokeGeometry
{
    /// <summary>True when (x, y) is within <paramref name="radius"/> of any segment of the polyline.</summary>
    public static bool IsNear(IReadOnlyList<(double X, double Y)> points, double x, double y, double radius)
    {
        if (points.Count == 0) return false;
        if (points.Count == 1) return Distance(points[0], (x, y)) <= radius;

        for (int i = 1; i < points.Count; i++)
        {
            if (DistanceToSegment(points[i - 1], points[i], x, y) <= radius) return true;
        }

        return false;
    }

    private static double DistanceToSegment((double X, double Y) a, (double X, double Y) b, double x, double y)
    {
        double dx = b.X - a.X, dy = b.Y - a.Y;
        double lengthSquared = dx * dx + dy * dy;
        if (lengthSquared == 0) return Distance(a, (x, y));

        // Project the point onto the segment, clamped to its ends.
        double t = Math.Clamp(((x - a.X) * dx + (y - a.Y) * dy) / lengthSquared, 0, 1);
        return Distance((a.X + t * dx, a.Y + t * dy), (x, y));
    }

    private static double Distance((double X, double Y) a, (double X, double Y) b) =>
        Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));
}
