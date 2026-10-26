using System.Numerics;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Effects;
using Microsoft.Graphics.Canvas.Geometry;
using Microsoft.Graphics.Canvas.Text;
using PinPoint.Core.Boards;
using PinPoint.Core.Capture;
using PinPoint.Core.Geometry;
using PinPoint.Core.Interop;
using PinPoint.Views;
using Windows.Foundation;
using Windows.Graphics.DirectX;
using Windows.UI;

namespace PinPoint.Services;

/// <summary>Something drawn on a board image. Bounds are screen pixels.</summary>
public abstract record CollageItem(PixelRect Bounds);

/// <summary>A snip with its drawing, or a frozen window. The picture is in memory or in a PNG file.</summary>
public sealed record CollageImage(PixelRect Bounds, CapturedFrame? Frame, string? FilePath, IReadOnlyList<double[]> Strokes)
    : CollageItem(Bounds);

/// <summary>A note, drawn the way its window looks. <paramref name="DpiScale"/> sizes its text as it was on screen.</summary>
public sealed record CollageNote(PixelRect Bounds, string Title, string Text, double DpiScale) : CollageItem(Bounds);

/// <summary>
/// Draws a board as one picture (Win2D, off the UI thread): everything where it was on screen, cropped to
/// what's there, with the rounded corners and shadows the pin windows have. Used for exports and thumbnails.
/// </summary>
public static class CollageRenderer
{
    private const float CornerRadius = 8; // Windows 11 window corners, in DIPs

    /// <summary>A PNG of the items, listed bottom to top, or null if none of them could be drawn.</summary>
    public static Task<byte[]?> RenderPngAsync(IReadOnlyList<CollageItem> items, bool dark, int margin, int maxWidth, int maxHeight) =>
        Task.Run(async () =>
        {
            var device = CanvasDevice.GetSharedDevice();
            var bitmaps = new Dictionary<CollageImage, CanvasBitmap>(ReferenceEqualityComparer.Instance);
            try
            {
                foreach (var image in items.OfType<CollageImage>())
                {
                    if (await LoadAsync(device, image) is { } bitmap) bitmaps[image] = bitmap;
                }

                var drawable = items.Where(i => i is CollageNote || i is CollageImage image && bitmaps.ContainsKey(image)).ToList();
                int limit = device.MaximumBitmapSizeInPixels;
                if (CollageLayout.Fit(drawable.Select(i => i.Bounds), margin, Math.Min(maxWidth, limit), Math.Min(maxHeight, limit)) is not { } layout)
                    return null;

                using var target = new CanvasRenderTarget(device, layout.Width, layout.Height, 96);
                using (var ds = target.CreateDrawingSession())
                {
                    ds.Clear(dark ? Color.FromArgb(255, 43, 43, 43) : Color.FromArgb(255, 228, 228, 228));
                    double systemScale = Win32.GetDpiForSystem() / 96.0;

                    foreach (var item in drawable)
                    {
                        var (x, y, width, height) = layout.Map(item.Bounds);
                        var rect = new Rect(x, y, width, height);
                        float k = (float)layout.Scale;
                        float radius = CornerRadius * k * (float)(item is CollageNote note ? note.DpiScale : systemScale);

                        DrawShadow(ds, rect, radius, k, dark);
                        using (ds.CreateLayer(1, CanvasGeometry.CreateRoundedRectangle(ds, rect, radius, radius)))
                        {
                            if (item is CollageImage image) DrawImage(ds, bitmaps[image], image.Strokes, rect);
                            else DrawNote(ds, (CollageNote)item, rect, layout.Scale, dark);
                        }

                        ds.DrawRoundedRectangle(rect, radius, radius,
                            dark ? Color.FromArgb(51, 255, 255, 255) : Color.FromArgb(31, 0, 0, 0), Math.Max(1, k));
                    }
                }

                using var png = new MemoryStream();
                await target.SaveAsync(png.AsRandomAccessStream(), CanvasBitmapFileFormat.Png);
                return png.ToArray();
            }
            finally
            {
                foreach (var bitmap in bitmaps.Values) bitmap.Dispose();
            }
        });

    private static async Task<CanvasBitmap?> LoadAsync(CanvasDevice device, CollageImage image)
    {
        if (image.Frame is { } frame)
        {
            return CanvasBitmap.CreateFromBytes(device, frame.Pixels, frame.Width, frame.Height,
                DirectXPixelFormat.B8G8R8A8UIntNormalized, 96, CanvasAlphaMode.Premultiplied);
        }

        if (image.FilePath is not { } path || !File.Exists(path)) return null;
        try
        {
            using var file = File.OpenRead(path);
            return await CanvasBitmap.LoadAsync(device, file.AsRandomAccessStream(), 96);
        }
        catch (Exception ex)
        {
            Core.Diagnostics.Log.Error($"Could not load board image {path}", ex);
            return null;
        }
    }

    private static void DrawShadow(CanvasDrawingSession ds, Rect rect, float radius, float scale, bool dark)
    {
        using var shape = new CanvasCommandList(ds);
        using (var s = shape.CreateDrawingSession()) s.FillRoundedRectangle(rect, radius, radius, Color.FromArgb(255, 0, 0, 0));

        using var shadow = new ShadowEffect
        {
            Source = shape,
            BlurAmount = Math.Max(0.5f, 8 * scale),
            ShadowColor = Color.FromArgb(dark ? (byte)150 : (byte)70, 0, 0, 0),
        };
        ds.DrawImage(shadow, 0, 3 * scale);
    }

    /// <summary>The picture stretched to its on-screen size, with its drawing (strokes are in picture pixels).</summary>
    private static void DrawImage(CanvasDrawingSession ds, CanvasBitmap bitmap, IReadOnlyList<double[]> strokes, Rect rect)
    {
        ds.DrawImage(bitmap, rect, bitmap.Bounds, 1, CanvasImageInterpolation.HighQualityCubic);
        if (strokes.Count == 0) return;

        var size = bitmap.SizeInPixels;
        var previous = ds.Transform;
        ds.Transform = Matrix3x2.CreateScale((float)(rect.Width / size.Width), (float)(rect.Height / size.Height))
            * Matrix3x2.CreateTranslation((float)rect.X, (float)rect.Y);

        using var style = new CanvasStrokeStyle
        {
            StartCap = CanvasCapStyle.Round,
            EndCap = CanvasCapStyle.Round,
            LineJoin = CanvasLineJoin.Round,
        };

        foreach (double[] flat in strokes)
        {
            if (flat.Length < 2) continue;
            var path = new CanvasPathBuilder(ds);
            path.BeginFigure((float)flat[0], (float)flat[1]);
            if (flat.Length < 4) path.AddLine((float)flat[0] + 0.01f, (float)flat[1]); // a dot
            for (int i = 2; i + 1 < flat.Length; i += 2) path.AddLine((float)flat[i], (float)flat[i + 1]);
            path.EndFigure(CanvasFigureLoop.Open);

            using var geometry = CanvasGeometry.CreatePath(path);
            ds.DrawGeometry(geometry, ImagePinWindow.InkColor, (float)ImagePinWindow.InkThickness, style);
        }

        ds.Transform = previous;
    }

    /// <summary>Mirrors NotePinWindow: a header with the note icon and title over plain wrapped text.</summary>
    private static void DrawNote(CanvasDrawingSession ds, CollageNote note, Rect rect, double layoutScale, bool dark)
    {
        double k = note.DpiScale * layoutScale; // DIPs → image pixels
        var background = dark ? Color.FromArgb(255, 32, 32, 32) : Color.FromArgb(255, 243, 243, 243);
        var headerBackground = dark ? Color.FromArgb(255, 28, 28, 28) : Color.FromArgb(255, 238, 238, 238);
        var text = dark ? Color.FromArgb(255, 255, 255, 255) : Color.FromArgb(228, 0, 0, 0);
        var secondaryText = dark ? Color.FromArgb(197, 255, 255, 255) : Color.FromArgb(158, 0, 0, 0);

        double header = 28 * k;
        ds.FillRectangle(rect, background);
        ds.FillRectangle(new Rect(rect.X, rect.Y, rect.Width, Math.Min(header, rect.Height)), headerBackground);

        using var icon = new CanvasTextFormat
        {
            FontFamily = "Segoe Fluent Icons",
            FontSize = Size(12 * k),
            VerticalAlignment = CanvasVerticalAlignment.Center,
        };
        ds.DrawText("", Clamp(rect.X + 10 * k, rect.Y, 16 * k, header), secondaryText, icon);

        using var title = new CanvasTextFormat
        {
            FontFamily = "Segoe UI",
            FontSize = Size(12 * k),
            VerticalAlignment = CanvasVerticalAlignment.Center,
            WordWrapping = CanvasWordWrapping.NoWrap,
            TrimmingGranularity = CanvasTextTrimmingGranularity.Character,
            TrimmingSign = CanvasTrimmingSign.Ellipsis,
        };
        ds.DrawText(note.Title, Clamp(rect.X + 28 * k, rect.Y, rect.Width - 38 * k, header), secondaryText, title);

        using var body = new CanvasTextFormat
        {
            FontFamily = "Segoe UI",
            FontSize = Size(14 * k),
            WordWrapping = CanvasWordWrapping.Wrap,
        };
        var bodyRect = Clamp(rect.X + 12 * k, rect.Y + header + 10 * k, rect.Width - 24 * k, rect.Height - header - 20 * k);
        using (ds.CreateLayer(1, bodyRect)) ds.DrawText(note.Text, bodyRect, text, body);
    }

    private static float Size(double dips) => (float)Math.Max(0.5, dips);

    private static Rect Clamp(double x, double y, double width, double height) => new(x, y, Math.Max(0, width), Math.Max(0, height));
}
