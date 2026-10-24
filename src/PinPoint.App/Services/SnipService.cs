using PinPoint.Core.Capture;
using PinPoint.Core.Diagnostics;
using PinPoint.Core.Geometry;
using PinPoint.Core.Windows;
using PinPoint.Views;

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
