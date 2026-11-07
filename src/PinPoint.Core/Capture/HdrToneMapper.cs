namespace PinPoint.Core.Capture;

/// <summary>
/// Turns an HDR desktop frame (linear scRGB half floats, 1.0 = 80 nits, as Windows.Graphics.Capture returns
/// it with HDR on) into an ordinary sRGB picture, the way it looked on screen.
/// <list type="bullet">
/// <item>Brightness is scaled so Windows' "SDR content brightness" white becomes white, so web pages, guides and
/// apps come out exactly as they would without HDR.</item>
/// <item>A few small highlights brighter than that (an HDR icon, a sparkle) are simply clipped, keeping their colour.</item>
/// <item>Real HDR content (more than <see cref="HighlightShare"/> of the screen above white, e.g. a game's sky) is
/// compressed smoothly above <see cref="Knee"/> so its 99.9th-percentile brightness just reaches white.</item>
/// </list>
/// Every pixel is scaled as a whole rather than per channel, so a bright red stays red instead of clipping to
/// white (which is what makes GDI captures of an HDR desktop look blown out).
/// </summary>
public static unsafe class HdrToneMapper
{
    /// <summary>Below this (fraction of SDR white) nothing changes, even when there are highlights to fit in.</summary>
    public const float Knee = 0.75f;

    /// <summary>Highlights covering less than this share of the frame are clipped rather than making room for them.</summary>
    public const float HighlightShare = 0.002f;

    /// <summary>Highlights are fitted to this percentile, so a handful of extreme pixels can't dim everything.</summary>
    private const float PeakPercentile = 0.999f;

    private const int LutSize = 16384;
    private const float HistogramStep = 0.01f; // brightness above white, in fractions of white
    private const int HistogramBins = 6400;    // up to 64× white
    private const int Chunk = 1 << 16;         // pixels per parallel work item

    private static readonly byte[] s_toSrgb = BuildSrgbTable();

    /// <summary>
    /// Converts <paramref name="scRgb"/> (RGBA, 4 halves per pixel) to opaque BGRA8 in <paramref name="bgra"/>
    /// (4 bytes per pixel). <paramref name="sdrWhiteNits"/> is the monitor's SDR white level.
    /// </summary>
    public static void ToSdrBgra(ReadOnlySpan<Half> scRgb, float sdrWhiteNits, Span<byte> bgra)
    {
        int pixels = scRgb.Length / 4;
        if (bgra.Length < pixels * 4) throw new ArgumentException("Output buffer too small.", nameof(bgra));
        if (pixels == 0) return;

        float scale = DisplayColor.ScRgbWhiteNits / Math.Max(1, sdrWhiteNits);
        int chunks = (pixels + Chunk - 1) / Chunk;

        fixed (Half* source = scRgb)
        fixed (byte* target = bgra)
        {
            nint src = (nint)source, dst = (nint)target;

            // Pass 1: how much is brighter than white, and how bright (histogram of the excess).
            var histogram = new int[HistogramBins + 1];
            ForEachChunk(chunks, c =>
            {
                int[]? local = null;
                var span = Pixels(src, c, pixels);
                for (int i = 0; i < span.Length; i += 4)
                {
                    float m = MathF.Max((float)span[i], MathF.Max((float)span[i + 1], (float)span[i + 2])) * scale;
                    if (m > 1.001f) (local ??= new int[HistogramBins + 1])[Math.Min(HistogramBins, (int)((m - 1) / HistogramStep))]++;
                }

                if (local is null) return;
                lock (histogram)
                {
                    for (int b = 0; b < local.Length; b++) histogram[b] += local[b];
                }
            });

            long above = 0;
            foreach (int count in histogram) above += count;

            float peak = above > pixels * HighlightShare ? PercentilePeak(histogram, pixels) : 1;
            float headroom = (peak - Knee) / (1 - Knee); // the fitted brightest value, in "excess" units

            // Pass 2: map every pixel.
            ForEachChunk(chunks, c =>
            {
                var span = Pixels(src, c, pixels);
                var output = new Span<byte>((byte*)dst + (long)c * Chunk * 4, span.Length);
                for (int i = 0; i < span.Length; i += 4)
                {
                    float r = MathF.Max(0, (float)span[i] * scale); // negatives are out-of-gamut wide colour: clip
                    float g = MathF.Max(0, (float)span[i + 1] * scale);
                    float b = MathF.Max(0, (float)span[i + 2] * scale);

                    float m = MathF.Max(r, MathF.Max(g, b));
                    if (m > Knee)
                    {
                        float mapped = peak > 1 ? (m >= peak ? 1 : Compress(m, headroom)) : MathF.Min(m, 1);
                        float factor = mapped / m;
                        r *= factor;
                        g *= factor;
                        b *= factor;
                    }

                    output[i] = Encode(b);
                    output[i + 1] = Encode(g);
                    output[i + 2] = Encode(r);
                    output[i + 3] = 255;
                }
            });
        }
    }

    /// <summary>
    /// Runs <paramref name="body"/> for chunks 0..n−1 on all cores and waits. (Not Parallel.For: the trimmed
    /// release build drops System.Threading.Tasks.Parallel from its dependency list.)
    /// </summary>
    private static void ForEachChunk(int chunks, Action<int> body)
    {
        int next = -1;
        var workers = new Task[Math.Min(chunks, Environment.ProcessorCount)];
        for (int w = 0; w < workers.Length; w++)
        {
            workers[w] = Task.Run(() =>
            {
                int c;
                while ((c = Interlocked.Increment(ref next)) < chunks) body(c);
            });
        }

        Task.WaitAll(workers);
    }

    /// <summary>One work item's pixels (4 halves each).</summary>
    private static ReadOnlySpan<Half> Pixels(nint source, int chunk, int pixels)
    {
        int first = chunk * Chunk;
        return new ReadOnlySpan<Half>((Half*)source + (long)first * 4, Math.Min(Chunk, pixels - first) * 4);
    }

    /// <summary>The brightness that only (1 − <see cref="PeakPercentile"/>) of the frame exceeds.</summary>
    private static float PercentilePeak(int[] histogram, int pixels)
    {
        long allowed = (long)(pixels * (1 - PeakPercentile)), seen = 0;
        for (int b = histogram.Length - 1; b >= 0; b--)
        {
            seen += histogram[b];
            if (seen > allowed) return 1 + (b + 1) * HistogramStep;
        }

        return 1;
    }

    /// <summary>
    /// Extended Reinhard on the part above the knee: slope 1 at the knee (no visible seam), and the fitted peak
    /// (<paramref name="headroom"/>) lands exactly on white.
    /// </summary>
    private static float Compress(float m, float headroom)
    {
        float excess = (m - Knee) / (1 - Knee);
        float mapped = excess * (1 + excess / (headroom * headroom)) / (1 + excess);
        return Knee + (1 - Knee) * mapped;
    }

    private static byte Encode(float linear) => s_toSrgb[(int)(Math.Clamp(linear, 0, 1) * (LutSize - 1) + 0.5f)];

    private static byte[] BuildSrgbTable()
    {
        var table = new byte[LutSize];
        for (int i = 0; i < LutSize; i++)
        {
            double l = i / (double)(LutSize - 1);
            double s = l <= 0.0031308 ? 12.92 * l : 1.055 * Math.Pow(l, 1 / 2.4) - 0.055;
            table[i] = (byte)Math.Round(Math.Clamp(s, 0, 1) * 255);
        }

        return table;
    }
}
