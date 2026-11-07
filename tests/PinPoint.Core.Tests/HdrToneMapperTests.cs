using PinPoint.Core.Capture;

namespace PinPoint.Core.Tests;

public sealed class HdrToneMapperTests
{
    private const float SdrWhiteNits = 240; // a typical "SDR content brightness" with HDR on

    /// <summary>One scRGB pixel at the given brightness relative to SDR white (1 = white).</summary>
    private static Half[] Pixel(float r, float g, float b)
    {
        float k = SdrWhiteNits / DisplayColor.ScRgbWhiteNits;
        return [(Half)(r * k), (Half)(g * k), (Half)(b * k), (Half)1];
    }

    private static byte[] Map(params Half[][] pixels)
    {
        var input = pixels.SelectMany(p => p).ToArray();
        var output = new byte[pixels.Length * 4];
        HdrToneMapper.ToSdrBgra(input, SdrWhiteNits, output);
        return output;
    }

    [Fact]
    public void Plain_sdr_content_comes_out_exactly_as_shown()
    {
        // White, sRGB mid grey (linear 0.214), black and a red, in BGRA.
        var out_ = Map(Pixel(1, 1, 1), Pixel(0.2140f, 0.2140f, 0.2140f), Pixel(0, 0, 0), Pixel(1, 0, 0));

        Assert.Equal([255, 255, 255, 255], out_[0..4]);
        Assert.InRange(out_[4], 127, 129);
        Assert.Equal([0, 0, 0, 255], out_[8..12]);
        Assert.Equal([0, 0, 255, 255], out_[12..16]);
    }

    [Fact]
    public void Hdr_highlights_fit_in_keep_their_colour_and_leave_darker_content_alone()
    {
        // A red highlight 4× brighter than white, a white, and a mid grey below the knee.
        var out_ = Map(Pixel(4, 0.4f, 0.4f), Pixel(1, 1, 1), Pixel(0.2140f, 0.2140f, 0.2140f));

        Assert.Equal(255, out_[2]);           // brightest pixel reaches full red…
        Assert.True(out_[0] < 200);           // …and stays red instead of clipping to white
        Assert.InRange(out_[4], 220, 254);    // white dims a little to make room
        Assert.InRange(out_[8], 127, 129);    // mid grey untouched
    }

    [Fact]
    public void A_few_tiny_highlights_are_clipped_without_dimming_white()
    {
        // 1 bright red pixel among 10,000 white ones (0.01%, under the highlight share).
        var pixels = Enumerable.Repeat(Pixel(1, 1, 1), 10_000).Prepend(Pixel(6, 0.6f, 0.6f)).ToArray();

        var out_ = Map(pixels);

        Assert.Equal([0, 0, 0, 0], new[] { 255 - out_[4], 255 - out_[5], 255 - out_[6], 255 - out_[7] }); // white stays white
        Assert.Equal(255, out_[2]);   // the highlight is full red…
        Assert.True(out_[0] < 200);   // …not clipped to white
    }

    [Fact]
    public void A_big_frame_matches_pixel_by_pixel_across_parallel_chunks()
    {
        // More pixels than one work item, all mid grey: every one must come out the same.
        var out_ = Map(Enumerable.Repeat(Pixel(0.2140f, 0.2140f, 0.2140f), 70_000).ToArray());
        Assert.All(Enumerable.Range(0, 70_000), p => Assert.InRange(out_[p * 4], 127, 129));
    }

    [Fact]
    public void Out_of_gamut_negatives_are_clipped()
    {
        var out_ = Map([(Half)(-0.5f), (Half)3, (Half)0, (Half)1]);
        Assert.Equal(0, out_[2]);
        Assert.Equal(255, out_[1]);
    }
}
