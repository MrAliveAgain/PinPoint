namespace PinPoint.Core.Text;

/// <summary>An axis-aligned box in snip pixels (the snip's own image, not the screen).</summary>
public readonly record struct TextBox(double X, double Y, double Width, double Height)
{
    public double Right => X + Width;

    public double Bottom => Y + Height;

    public bool Contains(double x, double y) => x >= X && x <= Right && y >= Y && y <= Bottom;

    /// <summary>Distance from a point to the nearest edge (0 when inside).</summary>
    public double DistanceTo(double x, double y)
    {
        double dx = Math.Max(Math.Max(X - x, 0), x - Right);
        double dy = Math.Max(Math.Max(Y - y, 0), y - Bottom);
        return Math.Sqrt(dx * dx + dy * dy);
    }
}

/// <summary>One recognised word. <see cref="Row"/>/<see cref="Column"/> place it in the reconstructed layout.</summary>
public sealed record RecognizedWord(string Text, TextBox Bounds, int Row, int Column);

/// <summary>
/// Text found in a snip, in reading order. OCR engines return each table cell as its own line;
/// lines that sit side by side are regrouped into rows so a copied table pastes as a table
/// (cells separated by tabs, rows by new lines).
/// </summary>
public sealed class RecognizedText
{
    public static RecognizedText Empty { get; } = new([]);

    /// <param name="lines">OCR lines, each a list of its words with their boxes (any order).</param>
    public RecognizedText(IEnumerable<IReadOnlyList<(string Text, TextBox Bounds)>> lines)
    {
        var usable = lines
            .Select(l => l.Where(w => !string.IsNullOrWhiteSpace(w.Text)).ToList())
            .Where(l => l.Count > 0)
            .Select(l => (Words: l, Box: Union(l.Select(w => w.Bounds))))
            .OrderBy(l => l.Box.Y)
            .ToList();

        // Group lines into rows: a line joins a row when it overlaps the row's band vertically by at
        // least half of the shorter of the two heights.
        var rows = new List<List<(List<(string Text, TextBox Bounds)> Words, TextBox Box)>>();
        var rowBands = new List<TextBox>();
        foreach (var line in usable)
        {
            int match = rowBands.FindIndex(band => OverlapsVertically(band, line.Box));
            if (match < 0)
            {
                rows.Add([line]);
                rowBands.Add(line.Box);
            }
            else
            {
                rows[match].Add(line);
                rowBands[match] = Union([rowBands[match], line.Box]);
            }
        }

        var words = new List<RecognizedWord>();
        var order = Enumerable.Range(0, rows.Count).OrderBy(i => rowBands[i].Y).ToList();
        for (int r = 0; r < order.Count; r++)
        {
            var columns = rows[order[r]].OrderBy(l => l.Box.X).ToList();
            for (int c = 0; c < columns.Count; c++)
            {
                foreach (var (text, bounds) in columns[c].Words.OrderBy(w => w.Bounds.X))
                    words.Add(new RecognizedWord(text, bounds, r, c));
            }
        }

        Words = words;
    }

    /// <summary>Every word in reading order (row by row, left to right).</summary>
    public IReadOnlyList<RecognizedWord> Words { get; }

    public bool IsEmpty => Words.Count == 0;

    public string AllText => IsEmpty ? string.Empty : GetText(0, Words.Count - 1);

    /// <summary>
    /// Index of the word under a point, or the nearest word within <paramref name="maxDistance"/>
    /// (pass +∞ while dragging so the selection follows the pointer anywhere). -1 if none.
    /// </summary>
    public int HitTest(double x, double y, double maxDistance = 0)
    {
        int best = -1;
        double bestDistance = double.MaxValue;
        for (int i = 0; i < Words.Count; i++)
        {
            double d = Words[i].Bounds.DistanceTo(x, y);
            if (d < bestDistance)
            {
                bestDistance = d;
                best = i;
            }
        }

        return bestDistance <= maxDistance ? best : -1;
    }

    /// <summary>Text of words <paramref name="from"/>..<paramref name="to"/> (inclusive, either order).</summary>
    public string GetText(int from, int to)
    {
        if (IsEmpty) return string.Empty;
        (from, to) = (Math.Clamp(Math.Min(from, to), 0, Words.Count - 1), Math.Clamp(Math.Max(from, to), 0, Words.Count - 1));

        var text = new System.Text.StringBuilder();
        for (int i = from; i <= to; i++)
        {
            if (i > from)
            {
                var previous = Words[i - 1];
                var current = Words[i];
                text.Append(current.Row != previous.Row ? Environment.NewLine
                    : current.Column != previous.Column ? "\t"
                    : " ");
            }

            text.Append(Words[i].Text);
        }

        return text.ToString();
    }

    private static bool OverlapsVertically(TextBox a, TextBox b)
    {
        double overlap = Math.Min(a.Bottom, b.Bottom) - Math.Max(a.Y, b.Y);
        return overlap >= 0.5 * Math.Min(a.Height, b.Height);
    }

    private static TextBox Union(IEnumerable<TextBox> boxes)
    {
        double left = double.MaxValue, top = double.MaxValue, right = double.MinValue, bottom = double.MinValue;
        foreach (var b in boxes)
        {
            left = Math.Min(left, b.X);
            top = Math.Min(top, b.Y);
            right = Math.Max(right, b.Right);
            bottom = Math.Max(bottom, b.Bottom);
        }

        return new TextBox(left, top, right - left, bottom - top);
    }
}
