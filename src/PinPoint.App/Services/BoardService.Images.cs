using Microsoft.UI.Xaml;
using PinPoint.Core;
using PinPoint.Core.Boards;
using PinPoint.Core.Capture;
using PinPoint.Core.Diagnostics;
using PinPoint.Core.Geometry;
using PinPoint.Core.Interop;
using PinPoint.Core.Windows;
using PinPoint.Helpers;

namespace PinPoint.Services;

/// <summary>One pin in a board preview, with a picture of just that pin at its on-screen size.</summary>
public sealed record PreviewPin(CollageItem Item, byte[] Png)
{
    /// <summary>A snapshot of a pinned window (export only; never part of a board).</summary>
    public bool IsWindow { get; init; }
}

public sealed record BoardPreview(string Name, IReadOnlyList<PreviewPin> Pins);

/// <summary>Pictures of boards: exported images, the Boards page thumbnails and the bigger preview.</summary>
public sealed partial class BoardService
{
    /// <summary>Space around everything in an exported image, in screen pixels.</summary>
    private const int ExportMargin = 48;

    /// <summary>Space between pins when they're packed together.</summary>
    public const int PackGap = 24;

    private const int ThumbnailMargin = 64;
    private const int ThumbnailWidth = 320;
    private const int ThumbnailHeight = 180;

    private readonly Dictionary<string, Task<string?>> _thumbnails = [];

    /// <summary>
    /// What's pinned right now, each pin drawn on its own, stacked bottom to top like on screen (for exporting).
    /// With <paramref name="captureWindows"/>, pinned windows are captured as they look now and come along as
    /// extra pins (<see cref="PreviewPin.IsWindow"/>): only for the image, they never become part of a board.
    /// Call on the UI thread.
    /// </summary>
    public async Task<BoardPreview> GetLivePreviewAsync(bool captureWindows)
    {
        var layers = new List<(nint Handle, CollageItem Item, bool IsWindow)>();
        foreach (var snip in _pins.Snips)
            layers.Add((snip.Handle, new CollageImage(snip.ScreenBounds, snip.Frame, null, snip.GetStrokePoints()), false));
        foreach (var note in _pins.Notes)
            layers.Add((note.Handle, NoteItem(note.ToBoardNote(), WindowHelpers.GetScale(note.Handle)), false));

        if (captureWindows)
        {
            var handles = _pins.PinnedWindows.Select(p => p.Handle).ToList();
            var frames = await Task.Run(() => handles.Select(h => (Handle: h, Frame: WindowCapture.Capture(h))).ToList());
            foreach (var (handle, frame) in frames)
            {
                if (frame is null) Log.Warn($"Could not capture pinned window {handle} for a board image");
                else layers.Add((handle, new CollageImage(frame.Bounds, frame, null, []), true));
            }
        }

        // EnumWindows lists topmost first, so the highest index is drawn first.
        var zOrder = WindowEnumerator.TopLevelInZOrder().Select((handle, i) => (handle, i)).ToDictionary(p => p.handle, p => p.i);
        bool dark = IsDarkTheme;
        var pins = new List<PreviewPin>();
        foreach (var layer in layers.OrderByDescending(l => zOrder.GetValueOrDefault(l.Handle, int.MaxValue)))
        {
            if (await CollageRenderer.RenderItemPngAsync(layer.Item, dark) is { } png) pins.Add(new PreviewPin(layer.Item, png) { IsWindow = layer.IsWindow });
        }

        return new BoardPreview(CurrentName ?? "What's pinned", pins);
    }

    /// <summary>A saved board's pins, each drawn on its own at its on-screen size (the Boards page preview).</summary>
    public async Task<BoardPreview?> GetPreviewAsync(string id)
    {
        if (id == CurrentId) await FlushAsync(); // show the open board as it is now
        if (_store.Load(id) is not { } board) return null;

        bool dark = IsDarkTheme;
        var pins = new List<PreviewPin>();
        foreach (var item in ItemsFromDisk(_store, id, board))
        {
            if (await CollageRenderer.RenderItemPngAsync(item, dark) is { } png) pins.Add(new PreviewPin(item, png));
        }

        return new BoardPreview(board.Name, pins);
    }

    /// <summary>
    /// The exported picture of <paramref name="items"/> (listed bottom to top), packed together or as on screen,
    /// scaled down to fit the given size (int.MaxValue for full size). Null if there's nothing to draw.
    /// </summary>
    public static Task<byte[]?> RenderAsync(IReadOnlyList<CollageItem> items, bool packed, int maxWidth = int.MaxValue, int maxHeight = int.MaxValue) =>
        CollageRenderer.RenderPngAsync(Arrange(items, packed), IsDarkTheme, ExportMargin, maxWidth, maxHeight);

    /// <summary>The full-size exported picture's size in pixels, or null if there's nothing to draw.</summary>
    public static (int Width, int Height)? ExportSize(IReadOnlyList<CollageItem> items, bool packed) =>
        CollageLayout.Fit(Arrange(items, packed).Select(i => i.Bounds), ExportMargin, int.MaxValue, int.MaxValue) is { } layout
            ? (layout.Width, layout.Height)
            : null;

    /// <summary>Path of an up-to-date preview of a saved board (drawn when missing or stale), or null if it's empty.</summary>
    public Task<string?> GetThumbnailAsync(string id)
    {
        if (_store.IsThumbnailCurrent(id)) return Task.FromResult<string?>(_store.ThumbnailPath(id));
        if (_thumbnails.TryGetValue(id, out var pending)) return pending;

        var drawing = DrawThumbnailAsync(id);
        if (!drawing.IsCompleted) _thumbnails[id] = drawing;
        return drawing;
    }

    /// <summary>The same items moved into tidy rows (when <paramref name="packed"/>), sizes and order unchanged.</summary>
    public static IReadOnlyList<CollageItem> Arrange(IReadOnlyList<CollageItem> items, bool packed)
    {
        if (!packed) return items;
        var rects = CollagePacker.Pack([.. items.Select(i => i.Bounds)], PackGap);
        return [.. items.Select((item, i) => item with { Bounds = rects[i] })];
    }

    private async Task<string?> DrawThumbnailAsync(string id)
    {
        try
        {
            // Packed: at thumbnail size a board spread across a wide screen would be specks.
            if (await RenderFromDiskAsync(id, packed: true, ThumbnailMargin, ThumbnailWidth, ThumbnailHeight) is not { } png) return null;
            _store.SaveThumbnail(id, png);
            return _store.ThumbnailPath(id);
        }
        catch (Exception ex)
        {
            Log.Error($"Could not draw thumbnail for board {id}", ex);
            return null;
        }
        finally
        {
            _thumbnails.Remove(id);
        }
    }

    private Task<byte[]?> RenderFromDiskAsync(string id, bool packed, int margin, int maxWidth, int maxHeight) =>
        _store.Load(id) is { } board
            ? CollageRenderer.RenderPngAsync(Arrange(ItemsFromDisk(_store, id, board), packed), IsDarkTheme, margin, maxWidth, maxHeight)
            : Task.FromResult<byte[]?>(null);

    /// <summary>A saved board's snips (images from its folder) then notes, as drawn on screen.</summary>
    private static List<CollageItem> ItemsFromDisk(BoardStore store, string id, Board board)
    {
        double noteScale = Win32.GetDpiForSystem() / 96.0;
        return
        [
            .. board.Snips.Select(s => new CollageImage(ToPixelRect(s.Bounds), null, store.ImagePath(id, s.Id), s.Strokes)),
            .. board.Notes.Select(n => NoteItem(n, noteScale)),
        ];
    }

    private static CollageNote NoteItem(BoardNote note, double dpiScale) =>
        new(ToPixelRect(note.Bounds), note.ExportPath is { } path ? Path.GetFileName(path) : "Note", note.Text, dpiScale);

    private static PixelRect ToPixelRect(BoardRect r) => new(r.X, r.Y, r.Width, r.Height);

    /// <summary>Pin windows follow the app theme, so their pictures do too.</summary>
    private static bool IsDarkTheme => Application.Current.RequestedTheme == ApplicationTheme.Dark;

    /// <summary>
    /// Developer check without UI: <c>PinPoint.exe --board-selftest board-id out.png [thumbnail|packed|pin]</c>
    /// draws a saved board (dark theme); <c>pin</c> draws just its first pin, as the preview does.
    /// Exit code 0 = drawn, 2 = nothing to draw, 1 = error.
    /// </summary>
    public static int RunSelfTest(string id, string output, string? mode)
    {
        try
        {
            var store = new BoardStore(AppPaths.BoardsDirectory);
            if (store.Load(id) is not { } board) return 2;

            var items = ItemsFromDisk(store, id, board);
            byte[]? png = (mode switch
            {
                "thumbnail" => CollageRenderer.RenderPngAsync(Arrange(items, true), true, ThumbnailMargin, ThumbnailWidth, ThumbnailHeight),
                "packed" => CollageRenderer.RenderPngAsync(Arrange(items, true), true, ExportMargin, int.MaxValue, int.MaxValue),
                "pin" when items.Count > 0 => CollageRenderer.RenderItemPngAsync(items[0], true),
                _ => CollageRenderer.RenderPngAsync(items, true, ExportMargin, int.MaxValue, int.MaxValue),
            }).GetAwaiter().GetResult();
            if (png is null) return 2;

            File.WriteAllBytes(output, png);
            return 0;
        }
        catch (Exception ex)
        {
            Log.Error("Board self-test failed", ex);
            File.WriteAllText(output + ".error.txt", ex.ToString());
            return 1;
        }
    }
}
