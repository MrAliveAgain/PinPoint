using System.Diagnostics;
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
            CopyPngToClipboard(await Task.Run(() => EncodePngAsync(frame)));
        }
        catch (Exception ex)
        {
            Log.Error("Could not copy snip to clipboard", ex);
        }
    }

    /// <summary>Must be called from the UI thread (the clipboard is STA-bound).</summary>
    public static void CopyPngToClipboard(byte[] png)
    {
        var package = new DataPackage { RequestedOperation = DataPackageOperation.Copy };
        package.SetBitmap(RandomAccessStreamReference.CreateFromStream(new MemoryStream(png).AsRandomAccessStream()));
        Clipboard.SetContent(package);
    }

    /// <summary>Must be called from the UI thread (the clipboard is STA-bound).</summary>
    public static void CopyTextToClipboard(string text)
    {
        try
        {
            var package = new DataPackage { RequestedOperation = DataPackageOperation.Copy };
            package.SetText(text);
            Clipboard.SetContent(package);
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
