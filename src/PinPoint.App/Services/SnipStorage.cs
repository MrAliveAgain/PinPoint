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
            byte[] png = await Task.Run(() => EncodePngAsync(frame));
            var package = new DataPackage { RequestedOperation = DataPackageOperation.Copy };
            package.SetBitmap(RandomAccessStreamReference.CreateFromStream(new MemoryStream(png).AsRandomAccessStream()));
            Clipboard.SetContent(package);
        }
        catch (Exception ex)
        {
            Log.Error("Could not copy snip to clipboard", ex);
        }
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

    private static async Task<byte[]> EncodePngAsync(CapturedFrame frame)
    {
        using var buffer = new MemoryStream();
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, buffer.AsRandomAccessStream());
        encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore,
            (uint)frame.Width, (uint)frame.Height, 96, 96, frame.Pixels);
        await encoder.FlushAsync();
        return buffer.ToArray();
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
