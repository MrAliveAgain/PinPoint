using System.Runtime.InteropServices.WindowsRuntime;
using PinPoint.Core.Capture;
using PinPoint.Core.Diagnostics;
using PinPoint.Core.Geometry;
using PinPoint.Core.Text;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage;
using Windows.Storage.Streams;

namespace PinPoint.Services;

/// <summary>
/// Finds text in snips with the OCR engine built into Windows (offline, uses the user's
/// installed languages). Runs once per snip in the background; results are plain data.
/// </summary>
public sealed class OcrService
{
    /// <summary>Small UI text OCRs far better enlarged (2× fixed most button labels in testing).</summary>
    private const double PreferredScale = 2.0;

    private readonly OcrEngine? _engine = OcrEngine.TryCreateFromUserProfileLanguages();

    public OcrService()
    {
        if (_engine is null) Log.Warn("No OCR language available; text selection in snips is off.");
    }

    public bool IsAvailable => _engine is not null;

    public async Task<RecognizedText> RecognizeAsync(CapturedFrame frame)
    {
        if (_engine is null) return RecognizedText.Empty;

        try
        {
            var (bitmap, scaleX, scaleY) = await CreateBitmapAsync(frame);
            using (bitmap)
            {
                var result = await _engine.RecognizeAsync(bitmap);
                var lines = result.Lines.Select(line => (IReadOnlyList<(string, TextBox)>)line.Words
                    .Select(w => (w.Text, new TextBox(
                        w.BoundingRect.X / scaleX, w.BoundingRect.Y / scaleY,
                        w.BoundingRect.Width / scaleX, w.BoundingRect.Height / scaleY)))
                    .ToList());
                return new RecognizedText(lines);
            }
        }
        catch (Exception ex)
        {
            Log.Error("Text recognition failed", ex);
            return RecognizedText.Empty;
        }
    }

    /// <summary>
    /// Developer self-test (<c>PinPoint.exe --ocr-selftest image.png out.txt</c>): proves OCR still works in
    /// trimmed release builds. Writes the recognised text to <paramref name="outputPath"/>; returns an exit code.
    /// </summary>
    public static int RunSelfTest(string imagePath, string outputPath)
    {
        // Off the STA main thread: blocking on WinRT async work there can deadlock.
        return Task.Run(async () =>
        {
            try
            {
                var frame = await SnipStorage.LoadPngAsync(imagePath);
                var text = await new OcrService().RecognizeAsync(frame);
                await File.WriteAllTextAsync(outputPath, text.AllText);
                return text.IsEmpty ? 2 : 0;
            }
            catch (Exception ex)
            {
                await File.WriteAllTextAsync(outputPath, "ERROR: " + ex);
                return 1;
            }
        }).GetAwaiter().GetResult();
    }

    private static async Task<(SoftwareBitmap Bitmap, double ScaleX, double ScaleY)> CreateBitmapAsync(CapturedFrame frame)
    {
        var source = SoftwareBitmap.CreateCopyFromBuffer(frame.Pixels.AsBuffer(), BitmapPixelFormat.Bgra8,
            frame.Width, frame.Height, BitmapAlphaMode.Ignore);

        double scale = Math.Min(PreferredScale, OcrEngine.MaxImageDimension / (double)Math.Max(frame.Width, frame.Height));
        if (scale <= 1.0) return (source, 1, 1);

        // High-quality resize via WIC: encode uncompressed, decode back through a scaling transform.
        uint width = (uint)Math.Round(frame.Width * scale);
        uint height = (uint)Math.Round(frame.Height * scale);
        using var stream = new InMemoryRandomAccessStream();
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.BmpEncoderId, stream);
        encoder.SetSoftwareBitmap(source);
        await encoder.FlushAsync();
        source.Dispose();

        var decoder = await BitmapDecoder.CreateAsync(stream);
        var transform = new BitmapTransform
        {
            ScaledWidth = width,
            ScaledHeight = height,
            InterpolationMode = BitmapInterpolationMode.Fant,
        };
        var scaled = await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore, transform,
            ExifOrientationMode.IgnoreExifOrientation, ColorManagementMode.DoNotColorManage);
        return (scaled, width / (double)frame.Width, height / (double)frame.Height);
    }
}
