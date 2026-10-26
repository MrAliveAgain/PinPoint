using Microsoft.UI.Dispatching;
using PinPoint.Core;
using PinPoint.Core.Boards;
using PinPoint.Core.Capture;
using PinPoint.Core.Diagnostics;

namespace PinPoint.Services;

/// <summary>
/// Boards behave like documents: you open one, it saves itself as you work (moves, zoom, drawing, typing,
/// added or closed pins), and switching to another replaces what's pinned. Snips and notes take part;
/// pinned windows never do. The board that was open at exit is reopened on the next start.
/// With no board open, loose snips are saved the same way to a hidden session, so they come back after a
/// restart like loose notes do (notes keep their own store). Pictures of boards are in BoardService.Images.cs.
/// </summary>
public sealed partial class BoardService
{
    private static readonly TimeSpan AutosaveInterval = TimeSpan.FromSeconds(2);
    private const string SessionId = "loose-snips";

    private readonly BoardStore _store = new(AppPaths.BoardsDirectory, useRecycleBin: true);
    private readonly BoardStore _session = new(AppPaths.SessionDirectory);
    private readonly PinManager _pins;
    private readonly SettingsService _settings;
    private readonly DispatcherQueueTimer _autosave;
    private string? _lastSavedJson;
    private Task _writing = Task.CompletedTask;

    /// <summary>Above zero while pins are being swapped (opening, restoring): saving then would catch them half-done.</summary>
    private int _busy;
    private bool _stopped;

    public BoardService(DispatcherQueue dispatcher, PinManager pins, SettingsService settings)
    {
        _pins = pins;
        _settings = settings;
        _autosave = dispatcher.CreateTimer();
        _autosave.Interval = AutosaveInterval;
        _autosave.Tick += (_, _) => SaveIfChanged();
        // Started by RestoreLastAsync, once the last session is back on screen.
    }

    /// <summary>Raised when the open board or the list of boards changes.</summary>
    public event EventHandler? Changed;

    public string? CurrentId { get; private set; }

    public string? CurrentName { get; private set; }

    public bool IsBoardOpen => CurrentId is not null;

    /// <summary>Pins exist that aren't saved in a board (no board is open). Switching boards should offer to save them.</summary>
    public bool HasUnsavedPins => !IsBoardOpen && (_pins.HasSnips || _pins.HasNotes);

    public bool HasPinsToSave => _pins.HasSnips || _pins.HasNotes;

    public string BoardsFolder => _store.Root;

    public bool HasPinnedWindows => _pins.PinnedWindows.Count > 0;

    public IReadOnlyList<BoardInfo> List() => _store.List();

    /// <summary>A name for a board saved without asking (from the tray): "Board 26 Oct", made unique.</summary>
    public string SuggestName()
    {
        string name = $"Board {DateTime.Now:d MMM}";
        var taken = List().Select(b => b.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!taken.Contains(name)) return name;
        for (int i = 2; ; i++)
        {
            if (!taken.Contains($"{name} ({i})")) return $"{name} ({i})";
        }
    }

    /// <summary>Saves what's pinned now as a new board and keeps working in it. False if it couldn't be written.</summary>
    public async Task<bool> SaveAsNewAsync(string name)
    {
        await _writing; // a session save may be running; let it finish before the board takes over
        string id = BoardStore.CreateId();
        if (!await WriteAsync(id, name)) return false; // leave the notes where they are

        foreach (var note in _pins.Notes) note.MoveToBoard(); // the board owns them now
        DeleteSession();
        SetCurrent(id, name);
        Log.Info($"Saved new board {id}");
        return true;
    }

    /// <summary>Opens a board, replacing what's pinned. Callers ask about unsaved pins first.</summary>
    public async Task OpenAsync(string id)
    {
        if (_store.Load(id) is not { } board) return;

        _busy++;
        try
        {
            await FlushAsync();
            _pins.CloseSnipsAndNotes();
            DeleteSession(); // whatever was loose was saved into a board or let go when the user was asked
            SetCurrent(null, null);

            int snips = await RestoreSnipsAsync(_store, id, board);
            foreach (var note in board.Notes) _pins.RestoreBoardNote(note);

            SetCurrent(id, board.Name);
            _lastSavedJson = BoardStore.ToJson(Snapshot(board.Name));
            Log.Info($"Opened board {id} ({snips} snips, {board.Notes.Count} notes)");
        }
        finally
        {
            _busy--;
        }
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

    /// <summary>Moves a saved board to the Recycle Bin. Deleting the open one also closes its pins.</summary>
    public void Delete(string id)
    {
        if (id == CurrentId)
        {
            SetCurrent(null, null);
            _pins.CloseSnipsAndNotes();
        }

        _store.Delete(id);
        Log.Info($"Deleted board {id} (to the Recycle Bin)");
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Brings back what was pinned when PinPoint last exited: the open board, or else the loose snips.
    /// Starts autosaving afterwards.
    /// </summary>
    public async Task RestoreLastAsync()
    {
        try
        {
            if (_settings.Current.OpenBoardId is { } id && _store.Exists(id)) await OpenAsync(id);
            else await RestoreLooseSnipsAsync();
        }
        catch (Exception ex)
        {
            Log.Error("Could not restore the last session", ex);
        }
        finally
        {
            _autosave.Start();
        }
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
        _stopped = true; // pins are about to close; that must not be saved as "nothing pinned"
        _autosave.Stop();
        try
        {
            _writing.Wait(TimeSpan.FromSeconds(10));
        }
        catch (Exception ex)
        {
            Log.Error("Board save on exit failed", ex);
        }
    }

    private async Task RestoreLooseSnipsAsync()
    {
        if (_session.Load(SessionId) is not { Snips.Count: > 0 } saved) return;

        _busy++;
        try
        {
            int restored = await RestoreSnipsAsync(_session, SessionId, saved);
            _lastSavedJson = BoardStore.ToJson(CurrentTarget().Board);
            Log.Info($"Brought back {restored} loose snips");
        }
        finally
        {
            _busy--;
        }
    }

    /// <summary>Decodes a board's snip images off the UI thread, then pins them. Returns how many came back.</summary>
    private async Task<int> RestoreSnipsAsync(BoardStore store, string id, Board board)
    {
        var images = await Task.Run(async () =>
        {
            var loaded = new Dictionary<string, CapturedFrame>();
            foreach (var snip in board.Snips)
            {
                string path = store.ImagePath(id, snip.Id);
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

        return images.Count;
    }

    private void DeleteSession()
    {
        try
        {
            _session.Delete(SessionId);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn($"Could not clear the loose-snips session: {ex.Message}");
        }
    }

    private void SetCurrent(string? id, string? name)
    {
        CurrentId = id;
        CurrentName = name;
        _pins.BoardMode = id is not null;
        _settings.Update(s => s.OpenBoardId = id);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void SaveIfChanged()
    {
        if (_busy > 0 || _stopped || !_writing.IsCompleted) return; // next tick will catch up

        var (store, id, board) = CurrentTarget();
        string json = BoardStore.ToJson(board);
        if (json == _lastSavedJson) return;

        _lastSavedJson = json;
        if (store == _session && board.Snips.Count == 0 && !_session.Exists(SessionId)) return; // nothing to keep

        _writing = WriteSnapshotAsync(store, id, board, NewImages(store, id));
    }

    /// <summary>Where what's pinned is saved: the open board, or else the loose-snips session (snips only).</summary>
    private (BoardStore Store, string Id, Board Board) CurrentTarget() =>
        CurrentId is { } id
            ? (_store, id, Snapshot(CurrentName ?? "Board"))
            : (_session, SessionId, Snapshot("Loose snips") with { Notes = [] });

    private Task<bool> WriteAsync(string id, string name)
    {
        var board = Snapshot(name);
        _lastSavedJson = BoardStore.ToJson(board);
        return WriteSnapshotAsync(_store, id, board, NewImages(_store, id));
    }

    /// <summary>Encodes new snip images and writes everything, off the UI thread. False if it failed.</summary>
    private Task<bool> WriteSnapshotAsync(BoardStore store, string id, Board board, List<(string SnipId, CapturedFrame Frame)> images) =>
        Task.Run(async () =>
        {
            try
            {
                foreach (var (snipId, frame) in images)
                    store.SaveImage(id, snipId, await SnipStorage.EncodePngAsync(frame));
                store.Save(id, board);
                return true;
            }
            catch (Exception ex)
            {
                Log.Error($"Could not save board {id}", ex);
                _lastSavedJson = null; // try again on the next tick
                return false;
            }
        });

    /// <summary>Snips whose image isn't in the folder yet (images never change once written).</summary>
    private List<(string, CapturedFrame)> NewImages(BoardStore store, string id) =>
        [.. _pins.Snips.Where(s => !File.Exists(store.ImagePath(id, s.Id))).Select(s => (s.Id, s.Frame))];

    /// <summary>The pinned snips and notes as board data (must run on the UI thread).</summary>
    private Board Snapshot(string name) => new()
    {
        Name = name,
        Snips =
        [
            .. _pins.Snips.Select(s =>
            {
                var r = s.ScreenBounds;
                return new BoardSnip(s.Id, new BoardRect(r.X, r.Y, r.Width, r.Height), s.CurrentZoomStep, s.PinOpacity, s.GetStrokePoints())
                {
                    Locked = s.IsLocked,
                };
            }),
        ],
        Notes = [.. _pins.Notes.Select(n => n.ToBoardNote())],
    };
}
