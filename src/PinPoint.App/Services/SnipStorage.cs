using System.Diagnostics;
using System.Runtime.InteropServices.WindowsRuntime;
using PinPoint.Core.Capture;
using PinPoint.Core.Diagnostics;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace PinPoint.Services;

/// <summary>PNG encoding, the snips folder and the clipboard.</summary>
public sealed class SnipStorage(SettingsService settings)
{
    public static string CreateFileName() => $"Snip {DateTime.Now:yyyy-MM-dd HH-mm-ss}.png";

    /// <summary>Saves into the configured snips folder (if enabled). Encoding runs off the UI thread.</summary>
    public async Task<string?> SaveToSnipFolderAsync(CapturedFrame frame)
    {
        if (!settings.Current.SaveSnips) return null;

        try
        {
            string folder = settings.SnipFolder;
            Directory.CreateDirectory(folder);
            string path = UniquePath(Path.Combine(folder, CreateFileName()));
            await SaveToAsync(frame, path);
            return path;
        }
        catch (Exception ex)
        {
            Log.Error("Could not save snip", ex);
            return null;
        }
    }

    public async Task SaveToAsync(CapturedFrame frame, string path)
    {
        byte[] png = await Task.Run(() => EncodePngAsync(frame));
        await File.WriteAllBytesAsync(path, png);
    }

    /// <summary>Must be called from the UI thread (the clipboard is STA-bound).</summary>
    public async Task CopyToClipboardAsync(CapturedFrame frame)
    {
        try
        {
            await CopyPngToClipboardAsync(await Task.Run(() => EncodePngAsync(frame)));
        }
        catch (Exception ex)
        {
            Log.Error("Could not copy snip to clipboard", ex);
        }
    }

    /// <summary>
    /// Puts a PNG on the clipboard as a picture other apps can paste. It must be a native WinRT stream: a
    /// .NET MemoryStream wrapper can't be read back by the clipboard, which silently leaves it empty.
    /// Flushed, so the picture is really on the clipboard (and survives PinPoint closing).
    /// Must be called from the UI thread (the clipboard is STA-bound).
    /// </summary>
    public static async Task CopyPngToClipboardAsync(byte[] png)
    {
        var stream = new InMemoryRandomAccessStream();
        await stream.WriteAsync(png.AsBuffer());
        stream.Seek(0);

        var package = new DataPackage { RequestedOperation = DataPackageOperation.Copy };
        package.SetBitmap(RandomAccessStreamReference.CreateFromStream(stream));
        Clipboard.SetContent(package);
        Clipboard.Flush();
    }

    /// <summary>Must be called from the UI thread (the clipboard is STA-bound).</summary>
    public static void CopyTextToClipboard(string text)
    {
        try
        {
            var package = new DataPackage { RequestedOperation = DataPackageOperation.Copy };
            package.SetText(text);
            Clipboard.SetContent(package);
            Clipboard.Flush(); // keep it after PinPoint closes
        }
        catch (Exception ex)
        {
            Log.Error("Could not copy text to clipboard", ex);
        }
    }

    public void OpenFolder()
    {
        string folder = settings.SnipFolder;
        Directory.CreateDirectory(folder);
        Process.Start(new ProcessStartInfo(folder) { UseShellExecute = true });
    }

    public static async Task<byte[]> EncodePngAsync(CapturedFrame frame)
    {
        using var buffer = new MemoryStream();
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, buffer.AsRandomAccessStream());
        encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore,
            (uint)frame.Width, (uint)frame.Height, 96, 96, frame.Pixels);
        await encoder.FlushAsync();
        return buffer.ToArray();
    }

    /// <summary>Loads an image file as an opaque frame (boards, OCR self-test). Position is (0, 0).</summary>
    public static async Task<CapturedFrame> LoadPngAsync(string path)
    {
        var file = await Windows.Storage.StorageFile.GetFileFromPathAsync(Path.GetFullPath(path));
        using var stream = await file.OpenAsync(Windows.Storage.FileAccessMode.Read);
        var decoder = await BitmapDecoder.CreateAsync(stream);
        using var bitmap = await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore);

        var pixels = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4];
        bitmap.CopyToBuffer(System.Runtime.InteropServices.WindowsRuntime.WindowsRuntimeBufferExtensions.AsBuffer(pixels));
        for (int i = 3; i < pixels.Length; i += 4) pixels[i] = 255; // XAML treats BGRA as premultiplied
        return new CapturedFrame(new Core.Geometry.PixelRect(0, 0, bitmap.PixelWidth, bitmap.PixelHeight), pixels);
    }

    /// <summary>
    /// Loads any image Windows can decode (PNG, JPEG, GIF, BMP, WebP…) as an opaque frame at (0, 0), scaled
    /// down to fit <paramref name="maxWidth"/>×<paramref name="maxHeight"/>. Transparent parts become white.
    /// </summary>
    public static async Task<CapturedFrame> LoadImageAsync(IRandomAccessStream stream, int maxWidth, int maxHeight)
    {
        var decoder = await BitmapDecoder.CreateAsync(stream);
        double scale = Math.Min(1, Math.Min(maxWidth / (double)decoder.PixelWidth, maxHeight / (double)decoder.PixelHeight));
        var transform = new BitmapTransform
        {
            ScaledWidth = (uint)Math.Max(1, Math.Round(decoder.PixelWidth * scale)),
            ScaledHeight = (uint)Math.Max(1, Math.Round(decoder.PixelHeight * scale)),
            InterpolationMode = BitmapInterpolationMode.Fant,
        };

        var data = await decoder.GetPixelDataAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, transform,
            ExifOrientationMode.IgnoreExifOrientation, ColorManagementMode.ColorManageToSRgb);
        byte[] pixels = data.DetachPixelData();

        // Premultiplied, so "over white" is just adding the missing coverage to each channel.
        for (int i = 0; i < pixels.Length; i += 4)
        {
            int missing = 255 - pixels[i + 3];
            if (missing == 0) continue;
            pixels[i] = (byte)(pixels[i] + missing);
            pixels[i + 1] = (byte)(pixels[i + 1] + missing);
            pixels[i + 2] = (byte)(pixels[i + 2] + missing);
            pixels[i + 3] = 255;
        }

        return new CapturedFrame(new Core.Geometry.PixelRect(0, 0, (int)transform.ScaledWidth, (int)transform.ScaledHeight), pixels);
    }

    private static string UniquePath(string path)
    {
        if (!File.Exists(path)) return path;
        string directory = Path.GetDirectoryName(path)!;
        string name = Path.GetFileNameWithoutExtension(path);
        for (int i = 2; ; i++)
        {
            string candidate = Path.Combine(directory, $"{name} ({i}).png");
            if (!File.Exists(candidate)) return candidate;
        }
    }
}
