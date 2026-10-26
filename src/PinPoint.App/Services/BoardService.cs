using Microsoft.UI.Dispatching;
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

/// <summary>
/// Boards behave like documents: you open one, it saves itself as you work (moves, zoom, drawing, typing,
/// added or closed pins), and switching to another replaces what's pinned. Snips and notes take part;
/// pinned windows never do. The board that was open at exit is reopened on the next start.
/// </summary>
public sealed class BoardService
{
    private static readonly TimeSpan AutosaveInterval = TimeSpan.FromSeconds(2);

    /// <summary>Space around everything in an exported image, in screen pixels.</summary>
    private const int ExportMargin = 48;
    private const int ThumbnailMargin = 64;
    private const int ThumbnailWidth = 320;
    private const int ThumbnailHeight = 180;

    private readonly Dictionary<string, Task<string?>> _thumbnails = [];

    private readonly BoardStore _store = new(AppPaths.BoardsDirectory);
    private readonly PinManager _pins;
    private readonly SettingsService _settings;
    private readonly DispatcherQueueTimer _autosave;
    private string? _lastSavedJson;
    private Task _writing = Task.CompletedTask;

    public BoardService(DispatcherQueue dispatcher, PinManager pins, SettingsService settings)
    {
        _pins = pins;
        _settings = settings;
        _autosave = dispatcher.CreateTimer();
        _autosave.Interval = AutosaveInterval;
        _autosave.Tick += (_, _) => SaveIfChanged();
    }

    /// <summary>Raised when the open board or the list of boards changes.</summary>
    public event EventHandler? Changed;

    public string? CurrentId { get; private set; }

    public string? CurrentName { get; private set; }

    public bool IsBoardOpen => CurrentId is not null;

    /// <summary>Pins exist that aren't saved anywhere (no board is open). Switching boards should offer to save them.</summary>
    public bool HasUnsavedPins => !IsBoardOpen && (_pins.HasSnips || _pins.HasNotes);

    public bool HasPinsToSave => _pins.HasSnips || _pins.HasNotes;

    public string BoardsFolder => _store.Root;

    public bool HasPinnedWindows => _pins.PinnedWindows.Count > 0;

    public IReadOnlyList<BoardInfo> List() => _store.List();

    // ---- Images (export and thumbnails) -------------------------------------------------------

    /// <summary>
    /// What's pinned right now as one PNG, laid out and stacked like on screen. With
    /// <paramref name="includeWindows"/>, pinned windows are captured as they look now; that's only in the
    /// image, they never become part of a board. Null if there's nothing to draw. Call on the UI thread.
    /// </summary>
    public async Task<byte[]?> RenderCurrentAsync(bool includeWindows)
    {
        var layers = new List<(nint Handle, CollageItem Item)>();
        foreach (var snip in _pins.Snips)
            layers.Add((snip.Handle, new CollageImage(snip.ScreenBounds, snip.Frame, null, snip.GetStrokePoints())));
        foreach (var note in _pins.Notes)
            layers.Add((note.Handle, NoteItem(note.ToBoardNote(), WindowHelpers.GetScale(note.Handle))));

        if (includeWindows)
        {
            var handles = _pins.PinnedWindows.Select(p => p.Handle).ToList();
            var frames = await Task.Run(() => handles.Select(h => (Handle: h, Frame: WindowCapture.Capture(h))).ToList());
            foreach (var (handle, frame) in frames)
            {
                if (frame is null) Log.Warn($"Could not capture pinned window {handle} for a board image");
                else layers.Add((handle, new CollageImage(frame.Bounds, frame, null, [])));
            }
        }

        // EnumWindows lists topmost first, so the highest index is drawn first.
        var zOrder = WindowEnumerator.TopLevelInZOrder().Select((handle, i) => (handle, i)).ToDictionary(p => p.handle, p => p.i);
        var items = layers.OrderByDescending(l => zOrder.GetValueOrDefault(l.Handle, int.MaxValue)).Select(l => l.Item).ToList();
        return await CollageRenderer.RenderPngAsync(items, IsDarkTheme, ExportMargin, int.MaxValue, int.MaxValue);
    }

    /// <summary>A saved board as one PNG, from its files. Null if it's empty or gone.</summary>
    public Task<byte[]?> RenderSavedAsync(string id) => RenderFromDiskAsync(id, ExportMargin, int.MaxValue, int.MaxValue);

    /// <summary>Path of an up-to-date preview of a saved board (drawn when missing or stale), or null if it's empty.</summary>
    public Task<string?> GetThumbnailAsync(string id)
    {
        if (_store.IsThumbnailCurrent(id)) return Task.FromResult<string?>(_store.ThumbnailPath(id));
        if (_thumbnails.TryGetValue(id, out var pending)) return pending;

        var drawing = DrawThumbnailAsync(id);
        if (!drawing.IsCompleted) _thumbnails[id] = drawing;
        return drawing;
    }

    private async Task<string?> DrawThumbnailAsync(string id)
    {
        try
        {
            if (await RenderFromDiskAsync(id, ThumbnailMargin, ThumbnailWidth, ThumbnailHeight) is not { } png) return null;
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

    private Task<byte[]?> RenderFromDiskAsync(string id, int margin, int maxWidth, int maxHeight) =>
        _store.Load(id) is { } board
            ? CollageRenderer.RenderPngAsync(ItemsFromDisk(_store, id, board), IsDarkTheme, margin, maxWidth, maxHeight)
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

    /// <summary>
    /// Developer check without UI: <c>PinPoint.exe --board-selftest board-id out.png [thumbnail]</c> draws a saved
    /// board (dark theme). Exit code 0 = drawn, 2 = nothing to draw, 1 = error.
    /// </summary>
    public static int RunSelfTest(string id, string output, bool thumbnail)
    {
        try
        {
            var store = new BoardStore(AppPaths.BoardsDirectory);
            if (store.Load(id) is not { } board) return 2;

            var items = ItemsFromDisk(store, id, board);
            byte[]? png = (thumbnail
                ? CollageRenderer.RenderPngAsync(items, true, ThumbnailMargin, ThumbnailWidth, ThumbnailHeight)
                : CollageRenderer.RenderPngAsync(items, true, ExportMargin, int.MaxValue, int.MaxValue)).GetAwaiter().GetResult();
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

    private static CollageNote NoteItem(BoardNote note, double dpiScale) =>
        new(ToPixelRect(note.Bounds), note.ExportPath is { } path ? Path.GetFileName(path) : "Note", note.Text, dpiScale);

    private static PixelRect ToPixelRect(BoardRect r) => new(r.X, r.Y, r.Width, r.Height);

    /// <summary>Pin windows follow the app theme, so their pictures do too.</summary>
    private static bool IsDarkTheme => Application.Current.RequestedTheme == ApplicationTheme.Dark;

    /// <summary>Saves what's pinned now as a new board and keeps working in it. False if it couldn't be written.</summary>
    public async Task<bool> SaveAsNewAsync(string name)
    {
        string id = BoardStore.CreateId();
        if (!await WriteAsync(id, name)) return false; // leave the notes where they are

        foreach (var note in _pins.Notes) note.MoveToBoard(); // the board owns them now
        SetCurrent(id, name);
        Log.Info($"Saved new board {id}");
        return true;
    }

    /// <summary>Opens a board, replacing what's pinned. Callers ask about unsaved pins first.</summary>
    public async Task OpenAsync(string id)
    {
        if (_store.Load(id) is not { } board) return;

        await FlushAsync();
        _pins.CloseSnipsAndNotes();
        SetCurrent(null, null);

        // Decode images off the UI thread, then pin on it.
        var images = await Task.Run(async () =>
        {
            var loaded = new Dictionary<string, CapturedFrame>();
            foreach (var snip in board.Snips)
            {
                string path = _store.ImagePath(id, snip.Id);
                try
                {
                    if (File.Exists(path)) loaded[snip.Id] = await SnipStorage.LoadPngAsync(path);
                }
                catch (Exception ex)
                {
                    Log.Error($"Could not load board image {path}", ex);
                }
            }

            return loaded;
        });

        foreach (var snip in board.Snips)
        {
            if (images.TryGetValue(snip.Id, out var frame)) _pins.RestoreSnip(frame, snip);
        }

        foreach (var note in board.Notes) _pins.RestoreBoardNote(note);

        SetCurrent(id, board.Name);
        _lastSavedJson = BoardStore.ToJson(Snapshot(board.Name));
        Log.Info($"Opened board {id} ({images.Count} snips, {board.Notes.Count} notes)");
    }

    /// <summary>Saves and closes the open board, leaving nothing pinned (pinned windows stay).</summary>
    public async Task CloseBoardAsync()
    {
        if (!IsBoardOpen) return;
        await FlushAsync();
        SetCurrent(null, null);
        _pins.CloseSnipsAndNotes();
    }

    public void Rename(string id, string name)
    {
        if (id == CurrentId)
        {
            CurrentName = name;
            SaveIfChanged(); // the snapshot carries the new name
        }
        else
        {
            _store.Rename(id, name);
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Deletes a saved board. Deleting the open one also closes its pins.</summary>
    public void Delete(string id)
    {
        if (id == CurrentId)
        {
            SetCurrent(null, null);
            _pins.CloseSnipsAndNotes();
        }

        _store.Delete(id);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Reopens the board that was open when PinPoint last exited.</summary>
    public async Task RestoreLastAsync()
    {
        if (_settings.Current.OpenBoardId is { } id && _store.Exists(id)) await OpenAsync(id);
    }

    /// <summary>Writes any pending changes now (switching boards, exiting).</summary>
    public async Task FlushAsync()
    {
        SaveIfChanged();
        await _writing;
    }

    /// <summary>Blocking flush for app exit (the writes don't need the UI thread, so this can't deadlock).</summary>
    public void FlushForExit()
    {
        SaveIfChanged();
        try
        {
            _writing.Wait(TimeSpan.FromSeconds(10));
        }
        catch (Exception ex)
        {
            Log.Error("Board save on exit failed", ex);
        }
    }

    private void SetCurrent(string? id, string? name)
    {
        CurrentId = id;
        CurrentName = name;
        _pins.BoardMode = id is not null;
        if (id is null) _autosave.Stop();
        else _autosave.Start();

        _settings.Update(s => s.OpenBoardId = id);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void SaveIfChanged()
    {
        if (CurrentId is not { } id || CurrentName is not { } name) return;
        if (!_writing.IsCompleted) return; // next tick will catch up

        var board = Snapshot(name);
        string json = BoardStore.ToJson(board);
        if (json == _lastSavedJson) return;

        _lastSavedJson = json;
        _writing = WriteSnapshotAsync(id, board, NewImages(id));
    }

    private Task<bool> WriteAsync(string id, string name)
    {
        var board = Snapshot(name);
        _lastSavedJson = BoardStore.ToJson(board);
        return WriteSnapshotAsync(id, board, NewImages(id));
    }

    /// <summary>Encodes new snip images and writes everything, off the UI thread. False if it failed.</summary>
    private Task<bool> WriteSnapshotAsync(string id, Board board, List<(string SnipId, CapturedFrame Frame)> images) =>
        Task.Run(async () =>
        {
            try
            {
                foreach (var (snipId, frame) in images)
                    _store.SaveImage(id, snipId, await SnipStorage.EncodePngAsync(frame));
                _store.Save(id, board);
                return true;
            }
            catch (Exception ex)
            {
                Log.Error($"Could not save board {id}", ex);
                _lastSavedJson = null; // try again on the next tick
                return false;
            }
        });

    /// <summary>Snips whose image isn't in the board folder yet (images never change once written).</summary>
    private List<(string, CapturedFrame)> NewImages(string id) =>
        [.. _pins.Snips.Where(s => !File.Exists(_store.ImagePath(id, s.Id))).Select(s => (s.Id, s.Frame))];

    /// <summary>The pinned snips and notes as board data (must run on the UI thread).</summary>
    private Board Snapshot(string name) => new()
    {
        Name = name,
        Snips =
        [
            .. _pins.Snips.Select(s =>
            {
                var r = s.ScreenBounds;
                return new BoardSnip(s.Id, new BoardRect(r.X, r.Y, r.Width, r.Height), s.CurrentZoomStep, s.PinOpacity, s.GetStrokePoints());
            }),
        ],
        Notes = [.. _pins.Notes.Select(n => n.ToBoardNote())],
    };
}
