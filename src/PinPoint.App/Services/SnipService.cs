using PinPoint.Core.Capture;
using PinPoint.Core.Diagnostics;
using PinPoint.Core.Geometry;
using PinPoint.Core.Interop;
using PinPoint.Core.Windows;
using PinPoint.Views;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage.Streams;

namespace PinPoint.Services;

/// <summary>Capture → pick a region → pin it in place → save/copy in the background.</summary>
public sealed class SnipService(PinManager pins, SnipStorage storage, SettingsService settings, Notifier notifier)
{
    private readonly IScreenCapture _capture = new GdiScreenCapture();
    private SnipOverlayWindow? _overlay;

    public async void Start()
    {
        // Pressing the shortcut again while selecting cancels.
        if (_overlay is { IsSelecting: true })
        {
            _overlay.Cancel();
            return;
        }

        try
        {
            // Pinned windows stay below the overlay until the selection is done
            // (snipping from a pinned Chrome window or another snip is a normal use).
            PixelRect? selection;
            CapturedFrame? frame;
            using (pins.SuspendRaising())
            {
                // Capture first, before any of our UI appears, with faded pins shown solid.
                using (pins.ShowSolidForCapture())
                {
                    frame = _capture.Capture(GdiScreenCapture.VirtualScreen);
                }

                _overlay ??= new SnipOverlayWindow();
                var targets = WindowEnumerator.GetSnapTargets(exclude: _overlay.Handle);
                selection = await _overlay.SelectAsync(frame, targets);
            }

            if (selection is not { } area) return;

            var snip = frame.Crop(area);
            frame = null; // let the full-screen buffer go before the async work below

            var window = pins.PinSnip(snip);

            if (settings.Current.CopySnipsToClipboard) await storage.CopyToClipboardAsync(snip);
            window.SavedPath = await storage.SaveToSnipFolderAsync(snip);
        }
        catch (Exception ex)
        {
            Log.Error("Snip failed", ex);
            notifier.Show("", "Snip failed", ex.Message, force: true);
        }
        finally
        {
            ReleaseCaptureMemory();
        }
    }

    // ---- Pinning images that weren't snipped (clipboard, dropped files) -----------------------

    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".jfif", ".bmp", ".gif", ".webp", ".tif", ".tiff", ".ico", ".heic", ".avif",
    };

    /// <summary>Pins the image on the clipboard (a copied picture, a screenshot, or image files copied in Explorer).</summary>
    public async void PinFromClipboard()
    {
        try
        {
            if (await PinFromDataAsync(Clipboard.GetContent()) == 0)
                notifier.Show("", "No image to pin", "Copy a picture (or image files in Explorer) first, then try again.", force: true);
        }
        catch (Exception ex)
        {
            Log.Error("Pinning from the clipboard failed", ex);
            notifier.Show("", "Couldn't pin that image", ex.Message, force: true);
        }
    }

    /// <summary>Pins images from copied or dragged data, near the mouse. Returns how many were pinned.</summary>
    public async Task<int> PinFromDataAsync(DataPackageView data)
    {
        if (data.Contains(StandardDataFormats.StorageItems))
        {
            var files = (await data.GetStorageItemsAsync()).Select(item => item.Path).ToList();
            int pinned = await PinFilesAsync(files);
            if (pinned > 0) return pinned;
        }

        if (data.Contains(StandardDataFormats.Bitmap))
        {
            var bitmap = await data.GetBitmapAsync();
            using var stream = await bitmap.OpenReadAsync();
            await PinImageAsync(stream, 0);
            return 1;
        }

        return 0;
    }

    /// <summary>Pins image files (others are skipped), fanned out a little from the mouse. Returns how many were pinned.</summary>
    public async Task<int> PinFilesAsync(IEnumerable<string> paths)
    {
        int pinned = 0;
        foreach (string path in paths.Where(p => ImageExtensions.Contains(Path.GetExtension(p))).Take(20))
        {
            try
            {
                using var file = File.OpenRead(path);
                await PinImageAsync(file.AsRandomAccessStream(), pinned);
                pinned++;
            }
            catch (Exception ex)
            {
                Log.Error($"Could not pin image {path}", ex);
            }
        }

        return pinned;
    }

    /// <summary>Centred on the mouse, at most 60% of that monitor (big photos are scaled down), like any other snip after that.</summary>
    private async Task PinImageAsync(IRandomAccessStream stream, int index)
    {
        Win32.GetCursorPos(out var cursor);
        var (_, work) = WindowEnumerator.GetMonitorAt(cursor.X, cursor.Y);
        var image = await SnipStorage.LoadImageAsync(stream, work.Width * 3 / 5, work.Height * 3 / 5);

        int offset = index * 32;
        int x = Math.Clamp(cursor.X - image.Width / 2 + offset, work.X, Math.Max(work.X, work.Right - image.Width));
        int y = Math.Clamp(cursor.Y - image.Height / 2 + offset, work.Y, Math.Max(work.Y, work.Bottom - image.Height));
        pins.PinSnip(new CapturedFrame(new PixelRect(x, y, image.Width, image.Height), image.Pixels));
    }

    /// <summary>
    /// A snip briefly holds a full virtual-screen buffer (~20 MB per 3440×1440 monitor) on the large-object
    /// heap. Compact it away once we're idle so PinPoint's footprint drops back down between snips.
    /// </summary>
    private static void ReleaseCaptureMemory()
    {
        System.Runtime.GCSettings.LargeObjectHeapCompactionMode = System.Runtime.GCLargeObjectHeapCompactionMode.CompactOnce;
        GC.Collect(2, GCCollectionMode.Optimized, blocking: false, compacting: true);
    }

    public void Close()
    {
        _overlay?.Close();
        _overlay = null;
    }
}
