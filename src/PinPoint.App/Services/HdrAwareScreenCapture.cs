using System.Runtime.InteropServices;
using Microsoft.Graphics.Canvas;
using Microsoft.UI;
using PinPoint.Core.Capture;
using PinPoint.Core.Diagnostics;
using PinPoint.Core.Geometry;
using Windows.Foundation.Metadata;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;

namespace PinPoint.Services;

/// <summary>
/// Screen capture that gets HDR right. The GDI copy (<see cref="GdiScreenCapture"/>) is fast but HDR-blind: with HDR
/// on, Windows composes the desktop in linear scRGB and GDI's 8-bit squeeze blows it out. So monitors in HDR are
/// captured again with Windows.Graphics.Capture as real HDR (half floats) and tone-mapped like Snipping Tool does
/// (<see cref="HdrToneMapper"/>), then pasted over the GDI copy. SDR monitors stay on the GDI path, untouched.
/// If anything about the HDR path fails, that monitor keeps its GDI copy rather than failing the snip.
/// </summary>
public sealed class HdrAwareScreenCapture : IScreenCapture
{
    private static readonly TimeSpan FrameTimeout = TimeSpan.FromSeconds(1);

    private readonly GdiScreenCapture _gdi = new();
    private bool _borderlessAllowed;

    public HdrAwareScreenCapture()
    {
        // Lets the capture skip the yellow "being captured" border. Asked once, in the background.
        _ = Task.Run(async () =>
        {
            try
            {
                if (ApiInformation.IsMethodPresent(typeof(GraphicsCaptureAccess).FullName, nameof(GraphicsCaptureAccess.RequestAccessAsync)))
                {
                    var status = await GraphicsCaptureAccess.RequestAccessAsync(GraphicsCaptureAccessKind.Borderless);
                    _borderlessAllowed = status == Windows.Security.Authorization.AppCapabilityAccess.AppCapabilityAccessStatus.Allowed;
                }
            }
            catch (Exception ex)
            {
                Log.Warn($"Borderless capture not available: {ex.Message}");
            }
        });
    }

    public CapturedFrame Capture(PixelRect area)
    {
        var frame = _gdi.Capture(area);

        IReadOnlyList<DisplayColorInfo> displays;
        try
        {
            displays = DisplayColor.Query();
        }
        catch (Exception ex)
        {
            Log.Error("Could not read display colour modes", ex);
            return frame;
        }

        foreach (var display in displays.Where(d => d.AdvancedColor && !d.Bounds.Intersect(area).IsEmpty))
        {
            try
            {
                if (CaptureHdrMonitor(display) is { } hdr) frame.Paste(hdr);
                else Log.Warn($"HDR capture gave no frame for {display.Bounds}; using the GDI copy");
            }
            catch (Exception ex)
            {
                Log.Error($"HDR capture failed for {display.Bounds}; using the GDI copy", ex);
            }
        }

        return frame;
    }

    /// <summary>
    /// Developer check without UI: <c>PinPoint.exe --hdr-selftest report.txt</c> captures the screen the old (GDI)
    /// and new way and writes numbers only (no pictures): per monitor its colour mode, how long the HDR capture
    /// took, and how many pixels came out blown-out white each way. Exit code 0 = HDR path worked, 2 = no HDR
    /// monitor, 1 = error.
    /// </summary>
    public static int RunSelfTest(string reportPath)
    {
        var report = new System.Text.StringBuilder();
        try
        {
            var capture = new HdrAwareScreenCapture();
            Thread.Sleep(500); // let the borderless request settle
            int result = 2;
            foreach (var display in DisplayColor.Query())
            {
                report.AppendLine($"{display.Bounds}: HDR {display.AdvancedColor}, SDR white {display.SdrWhiteNits} nits");
                if (!display.AdvancedColor) continue;

                var gdi = capture._gdi.Capture(display.Bounds);
                var watch = System.Diagnostics.Stopwatch.StartNew();
                var hdr = capture.CaptureHdrMonitor(display);
                long first = watch.ElapsedMilliseconds;
                watch.Restart();
                var again = capture.CaptureScRgb(display);
                report.AppendLine(hdr is null
                    ? "  HDR capture: no frame"
                    : $"  HDR capture: {hdr.Width}×{hdr.Height} in {first} ms (again: {watch.ElapsedMilliseconds} ms raw)");
                report.AppendLine($"  blown-out white pixels: GDI {WhiteShare(gdi):P2}, HDR path {(hdr is null ? "-" : WhiteShare(hdr).ToString("P2"))}");
                if (again is { } raw) report.AppendLine("  " + Brightness(MemoryMarshal.Cast<byte, Half>(raw.Raw), display.SdrWhiteNits));
                report.AppendLine($"  capture border skipped: {capture._borderlessAllowed}");
                result = hdr is null ? 1 : 0;
            }

            File.WriteAllText(reportPath, report.ToString());
            return result;
        }
        catch (Exception ex)
        {
            File.WriteAllText(reportPath, report + ex.ToString());
            return 1;
        }

        static double WhiteShare(CapturedFrame frame)
        {
            int white = 0, n = frame.Pixels.Length / 4;
            for (int i = 0; i < frame.Pixels.Length; i += 4)
            {
                if (frame.Pixels[i] == 255 && frame.Pixels[i + 1] == 255 && frame.Pixels[i + 2] == 255) white++;
            }

            return n == 0 ? 0 : white / (double)n;
        }

        // Brightest channel per pixel, relative to SDR white (1.0 = white): peak and a few high percentiles.
        static string Brightness(ReadOnlySpan<Half> scRgb, float sdrWhiteNits)
        {
            float scale = DisplayColor.ScRgbWhiteNits / sdrWhiteNits;
            var values = new float[scRgb.Length / 4];
            for (int i = 0, p = 0; p < values.Length; i += 4, p++)
                values[p] = MathF.Max((float)scRgb[i], MathF.Max((float)scRgb[i + 1], (float)scRgb[i + 2])) * scale;
            Array.Sort(values);
            float At(double q) => values[(int)Math.Min(values.Length - 1, q * values.Length)];
            int above = values.Length - Array.FindIndex(values, v => v > 1.001f) is var a && a <= values.Length ? a : 0;
            return $"brightness vs SDR white: p50 {At(0.5):F3}, p99 {At(0.99):F3}, p99.9 {At(0.999):F3}, peak {values[^1]:F3}; above white: {above} px";
        }
    }

    /// <summary>One frame of the monitor as scRGB half floats, tone-mapped to an ordinary picture.</summary>
    private CapturedFrame? CaptureHdrMonitor(DisplayColorInfo display)
    {
        if (CaptureScRgb(display) is not var (width, height, raw)) return null;

        var pixels = new byte[width * height * 4];
        HdrToneMapper.ToSdrBgra(MemoryMarshal.Cast<byte, Half>(raw), display.SdrWhiteNits, pixels);
        return new CapturedFrame(new PixelRect(display.Bounds.X, display.Bounds.Y, width, height), pixels);
    }

    /// <summary>The monitor's current frame as raw scRGB (R, G, B, A halves, 8 bytes per pixel), or null.</summary>
    private (int Width, int Height, byte[] Raw)? CaptureScRgb(DisplayColorInfo display)
    {
        if (!GraphicsCaptureSession.IsSupported()
            || !ApiInformation.IsMethodPresent(typeof(GraphicsCaptureItem).FullName, nameof(GraphicsCaptureItem.TryCreateFromDisplayId)))
            return null;

        var displayId = Win32Interop.GetDisplayIdFromMonitor(display.Monitor);
        if (GraphicsCaptureItem.TryCreateFromDisplayId(new Windows.Graphics.DisplayId { Value = displayId.Value }) is not { } item) return null;

        var device = CanvasDevice.GetSharedDevice();
        using var pool = Direct3D11CaptureFramePool.CreateFreeThreaded(device, DirectXPixelFormat.R16G16B16A16Float, 1, item.Size);
        using var session = pool.CreateCaptureSession(item);
        session.IsCursorCaptureEnabled = false;
        if (_borderlessAllowed) session.IsBorderRequired = false;

        using var arrived = new ManualResetEventSlim();
        pool.FrameArrived += (_, _) => arrived.Set();
        session.StartCapture();
        if (!arrived.Wait(FrameTimeout)) return null;

        using var captured = pool.TryGetNextFrame();
        if (captured is null) return null;

        using var bitmap = CanvasBitmap.CreateFromDirect3D11Surface(device, captured.Surface);
        var size = bitmap.SizeInPixels;
        return ((int)size.Width, (int)size.Height, bitmap.GetPixelBytes());
    }
}
