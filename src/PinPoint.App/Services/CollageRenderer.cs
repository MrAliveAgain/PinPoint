using System.Numerics;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Brushes;
using Microsoft.Graphics.Canvas.Effects;
using Microsoft.Graphics.Canvas.Geometry;
using Microsoft.Graphics.Canvas.Text;
using PinPoint.Core.Boards;
using PinPoint.Core.Capture;
using PinPoint.Core.Geometry;
using PinPoint.Core.Interop;
using PinPoint.Core.Settings;
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
    public static Task<byte[]?> RenderPngAsync(IReadOnlyList<CollageItem> items, bool dark, int margin, int maxWidth, int maxHeight,
        CollageBackground? background = null) =>
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

                background ??= CollageBackground.Default;
                using var texture = await LoadTextureAsync(device, background);
                using var target = new CanvasRenderTarget(device, layout.Width, layout.Height, 96);
                using (var ds = target.CreateDrawingSession())
                {
                    DrawBackground(ds, layout.Width, layout.Height, (float)layout.Scale, background, texture, dark);
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
                            DrawContent(ds, item, bitmaps, rect, layout.Scale, dark);
                        }

                        ds.DrawRoundedRectangle(rect, radius, radius,
                            dark ? Color.FromArgb(51, 255, 255, 255) : Color.FromArgb(31, 0, 0, 0), Math.Max(1, k));
                    }
                }

                return await ToPngAsync(target);
            }
            finally
            {
                foreach (var bitmap in bitmaps.Values) bitmap.Dispose();
            }
        });

    /// <summary>
    /// Just one pin at its on-screen size, as a plain rectangle (a snip with its drawing, or a note as its
    /// window looks). Used by the board preview and for saving or copying a single snip from it.
    /// </summary>
    public static Task<byte[]?> RenderItemPngAsync(CollageItem item, bool dark) =>
        Task.Run(async () =>
        {
            var device = CanvasDevice.GetSharedDevice();
            var bitmaps = new Dictionary<CollageImage, CanvasBitmap>(ReferenceEqualityComparer.Instance);
            try
            {
                if (item is CollageImage image)
                {
                    if (await LoadAsync(device, image) is not { } bitmap) return null;
                    bitmaps[image] = bitmap;
                }

                int limit = device.MaximumBitmapSizeInPixels;
                if (CollageLayout.Fit([item.Bounds], 0, limit, limit) is not { } layout) return null;

                using var target = new CanvasRenderTarget(device, layout.Width, layout.Height, 96);
                using (var ds = target.CreateDrawingSession())
                {
                    ds.Clear(Color.FromArgb(0, 0, 0, 0));
                    var (x, y, width, height) = layout.Map(item.Bounds);
                    DrawContent(ds, item, bitmaps, new Rect(x, y, width, height), layout.Scale, dark);
                }

                return await ToPngAsync(target);
            }
            finally
            {
                foreach (var bitmap in bitmaps.Values) bitmap.Dispose();
            }
        });

    private static void DrawContent(CanvasDrawingSession ds, CollageItem item, Dictionary<CollageImage, CanvasBitmap> bitmaps, Rect rect, double scale, bool dark)
    {
        if (item is CollageImage image) DrawImage(ds, bitmaps[image], image.Strokes, rect);
        else DrawNote(ds, (CollageNote)item, rect, scale, dark);
    }

    private static async Task<byte[]> ToPngAsync(CanvasRenderTarget target)
    {
        using var png = new MemoryStream();
        await target.SaveAsync(png.AsRandomAccessStream(), CanvasBitmapFileFormat.Png);
        return png.ToArray();
    }

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

    // ---- Backgrounds -------------------------------------------------------------------------

    /// <summary>The background picture, if it uses one (a missing or broken file falls back to the default).</summary>
    private static async Task<CanvasBitmap?> LoadTextureAsync(CanvasDevice device, CollageBackground background)
    {
        if (background.Style != BackgroundStyle.Image || background.ImagePath is not { } path || !File.Exists(path)) return null;
        try
        {
            using var file = File.OpenRead(path);
            return await CanvasBitmap.LoadAsync(device, file.AsRandomAccessStream(), 96);
        }
        catch (Exception ex)
        {
            Core.Diagnostics.Log.Error($"Could not load background image {path}", ex);
            return null;
        }
    }

    /// <summary>
    /// Fills the picture behind the pins. A background image either covers it (cropped to keep its shape) or,
    /// as a tile, repeats at one image pixel per screen pixel, scaled with the picture so thumbnails match.
    /// </summary>
    private static void DrawBackground(CanvasDrawingSession ds, int width, int height, float scale, CollageBackground background,
        CanvasBitmap? texture, bool dark)
    {
        switch (background.Style)
        {
            case BackgroundStyle.Transparent:
                ds.Clear(Color.FromArgb(0, 0, 0, 0));
                break;

            case BackgroundStyle.Solid:
                ds.Clear(HexColor.TryParse(background.Color, out byte r, out byte g, out byte b) ? Color.FromArgb(255, r, g, b) : DefaultColor(dark));
                break;

            case BackgroundStyle.Image when texture is not null && background.Tile:
                using (var tiles = new CanvasImageBrush(ds, texture)
                {
                    ExtendX = CanvasEdgeBehavior.Wrap,
                    ExtendY = CanvasEdgeBehavior.Wrap,
                    Transform = Matrix3x2.CreateScale(scale),
                    Interpolation = CanvasImageInterpolation.HighQualityCubic,
                })
                {
                    ds.FillRectangle(0, 0, width, height, tiles);
                }

                break;

            case BackgroundStyle.Image when texture is not null:
                var size = texture.Size;
                double cover = Math.Max(width / size.Width, height / size.Height);
                double w = size.Width * cover, h = size.Height * cover;
                ds.DrawImage(texture, new Rect((width - w) / 2, (height - h) / 2, w, h), texture.Bounds, 1, CanvasImageInterpolation.HighQualityCubic);
                break;

            case BackgroundStyle.DotGrid:
                ds.Clear(DefaultColor(dark));
                float spacing = Math.Max(6, 24 * scale), dot = Math.Max(0.6f, 1.4f * scale);
                var dotColor = dark ? Color.FromArgb(70, 255, 255, 255) : Color.FromArgb(60, 0, 0, 0);
                for (float y = spacing / 2; y < height; y += spacing)
                {
                    for (float x = spacing / 2; x < width; x += spacing) ds.FillCircle(x, y, dot, dotColor);
                }

                break;

            default:
                ds.Clear(DefaultColor(dark));
                break;
        }
    }

    private static Color DefaultColor(bool dark) => dark ? Color.FromArgb(255, 43, 43, 43) : Color.FromArgb(255, 228, 228, 228);

    // ---- Pins --------------------------------------------------------------------------------

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
