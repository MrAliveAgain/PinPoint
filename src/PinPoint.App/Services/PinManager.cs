using System.Collections.ObjectModel;
using Microsoft.UI.Dispatching;
using PinPoint.Core;
using PinPoint.Core.Capture;
using PinPoint.Core.Interop;
using PinPoint.Core.Notes;
using PinPoint.Core.Windows;
using PinPoint.Helpers;
using PinPoint.ViewModels;
using PinPoint.Views;

namespace PinPoint.Services;

/// <summary>
/// Single source of truth for everything pinned: other apps' windows plus PinPoint's own surfaces
/// (snips and notes, see <see cref="IPinSurface"/>). Also keeps pins above games that make themselves topmost.
/// </summary>
public sealed class PinManager : IDisposable
{
    private readonly WindowPinService _windows = new();
    private readonly ForegroundWatcher _foreground = new();
    private readonly List<ImagePinWindow> _snips = [];
    private readonly List<NotePinWindow> _notes = [];
    private readonly NoteStore _noteStore = new(AppPaths.NotesDirectory);
    private readonly SettingsService _settings;
    private readonly SnipStorage _storage;
    private readonly OcrService _ocr;
    private readonly Notifier _notifier;
    private readonly DispatcherQueueTimer _maintenance;
    private readonly DispatcherQueueTimer _raiseAgain;
    private int _raiseSuspensions;

    public PinManager(DispatcherQueue dispatcher, SettingsService settings, SnipStorage storage, OcrService ocr, Notifier notifier)
    {
        _settings = settings;
        _storage = storage;
        _ocr = ocr;
        _notifier = notifier;

        _windows.HighlightColor = settings.HighlightColorRef;
        _windows.Pinned += pin => Items.Add(new WindowPinItem(pin, this));
        _windows.Unpinned += pin =>
        {
            if (Items.OfType<WindowPinItem>().FirstOrDefault(i => i.Pin == pin) is { } item) Items.Remove(item);
            OnStateChanged();
        };
        settings.Changed += (_, _) => _windows.HighlightColor = settings.HighlightColorRef;

        _raiseAgain = dispatcher.CreateTimer();
        _raiseAgain.Interval = TimeSpan.FromMilliseconds(300);
        _raiseAgain.IsRepeating = false;
        _raiseAgain.Tick += (_, _) => RaiseAll();

        // Games that are topmost themselves jump above pins when they get focus; push pins back up.
        _foreground.ForegroundChanged += _ =>
        {
            RaiseAll();
            _raiseAgain.Start(); // some games re-assert topmost a moment after activation
        };

        _maintenance = dispatcher.CreateTimer();
        _maintenance.Interval = TimeSpan.FromSeconds(1.5);
        _maintenance.Tick += (_, _) => Maintain();
        _maintenance.Start();
    }

    public ObservableCollection<PinItem> Items { get; } = [];

    public event EventHandler? StateChanged;

    public bool IsHidden { get; private set; }

    /// <summary>
    /// The global "click-through for everything" state: on only when every pin is click-through, so making one
    /// note click-through from its own button doesn't light up the app's and tray's all-pins toggle. Windows that
    /// can't be made click-through don't count.
    /// </summary>
    public bool IsClickThrough
    {
        get
        {
            var windows = _windows.Pins.Where(p => p.SupportsOpacity).ToList();
            bool any = Surfaces.Any() || windows.Count > 0;
            return any && Surfaces.All(s => s.IsClickThrough) && windows.All(p => p.IsClickThrough);
        }
    }

    public bool HasPins => Items.Count > 0;

    public bool HasSnips => _snips.Count > 0;

    public string NotesFolder => _noteStore.Directory;

    /// <summary>Every PinPoint-owned pin window (snips and notes), treated alike for raise/hide/fade.</summary>
    private IEnumerable<IPinSurface> Surfaces => _snips.Cast<IPinSurface>().Concat(_notes);

    // ---- Window pins --------------------------------------------------------------------------

    public void TogglePinForegroundWindow()
    {
        nint hwnd = Win32.GetForegroundWindow();
        if (hwnd == 0) return;

        if (Win32.GetProcessId(hwnd) == Environment.ProcessId)
        {
            _notifier.Show("", "Already on top", "PinPoint's own windows stay pinned.");
            return;
        }

        ShowAll();
        switch (_windows.Toggle(hwnd))
        {
            case PinResult.Pinned:
                _notifier.Show("", "Pinned on top", _windows.Find(hwnd)?.Title);
                break;
            case PinResult.Unpinned:
                _notifier.Show("", "Unpinned", Win32.GetWindowText(hwnd));
                break;
            case PinResult.NotPinnable:
                _notifier.Show("", "Can't pin that", "Click into the window you want to pin, then press the shortcut.");
                break;
            case PinResult.Failed:
                _notifier.Show("", "Couldn't pin that window", "It's running as administrator. Run PinPoint as admin to pin it.");
                break;
        }

        OnStateChanged();
    }

    public void PinWindow(nint hwnd)
    {
        ShowAll();
        var (result, pin) = _windows.Pin(hwnd);
        if (result == PinResult.Pinned) _notifier.Show("", "Pinned on top", pin?.Title);
        else if (result == PinResult.Failed) _notifier.Show("", "Couldn't pin that window", "It's running as administrator.");
        OnStateChanged();
    }

    public bool IsWindowPinned(nint hwnd) => _windows.Find(hwnd) is not null;

    public void Unpin(PinnedWindow pin) => _windows.Unpin(pin);

    public void UnpinWindow(nint hwnd)
    {
        if (_windows.Find(hwnd) is { } pin) _windows.Unpin(pin);
    }

    public void SetOpacity(PinnedWindow pin, double opacity) => _windows.SetOpacity(pin, opacity);

    // ---- Snip pins ----------------------------------------------------------------------------

    public ImagePinWindow PinSnip(CapturedFrame frame)
    {
        ShowAll();
        var window = new ImagePinWindow(frame, _storage, _ocr, _settings);
        window.Closed += (_, _) => OnSnipClosed(window);
        _snips.Add(window);
        Items.Add(new SnipPinItem(window));
        window.ShowAt(frame.Bounds);
        OnStateChanged();
        return window;
    }

    public void CloseAllSnips()
    {
        foreach (var snip in _snips.ToArray()) snip.Close();
    }

    private void OnSnipClosed(ImagePinWindow window)
    {
        _snips.Remove(window);
        if (Items.OfType<SnipPinItem>().FirstOrDefault(i => i.Window == window) is { } item) Items.Remove(item);
        OnStateChanged();
    }

    // ---- Notes --------------------------------------------------------------------------------

    /// <summary>Opens a new, empty note near the mouse and puts the cursor in it.</summary>
    public void CreateNote()
    {
        ShowAll();
        // With a board open, new notes belong to the board (it saves them); otherwise they're loose notes.
        AddNote(new StoredNote(NoteStore.CreateId(), string.Empty, null), activate: true, BoardMode ? null : _noteStore);
    }

    /// <summary>
    /// A note's + button: an empty note right beside it (or wherever it fits), with the same width and
    /// opacity but the default height.
    /// </summary>
    private void CreateNoteBeside(NotePinWindow source)
    {
        var bounds = source.WindowBounds;
        var (_, work) = WindowEnumerator.GetMonitorAt(bounds.X + bounds.Width / 2, bounds.Y + bounds.Height / 2);
        var others = _notes.Where(n => n != source).Select(n => n.WindowBounds).ToList();
        double scale = source.Scale;
        int height = (int)Math.Round(NotePinWindow.DefaultHeight * scale);
        var spot = NotePlacement.Beside(bounds, bounds.Width, height, work, others, gap: (int)Math.Round(8 * scale));

        var layout = new NoteLayout(spot.X, spot.Y, spot.Width, spot.Height, source.PinOpacity);
        // Same rule as CreateNote: with a board open the new note belongs to the board.
        AddNote(new StoredNote(NoteStore.CreateId(), string.Empty, layout), activate: true, BoardMode ? null : _noteStore);
    }

    /// <summary>A note's menu → Duplicate: same text, size and opacity, beside it (not tied to its .txt file).</summary>
    private void DuplicateNote(NotePinWindow source)
    {
        var bounds = source.WindowBounds;
        var (_, work) = WindowEnumerator.GetMonitorAt(bounds.X + bounds.Width / 2, bounds.Y + bounds.Height / 2);
        var others = _notes.Where(n => n != source).Select(n => n.WindowBounds).ToList();
        var spot = NotePlacement.Beside(bounds, bounds.Width, bounds.Height, work, others, gap: (int)Math.Round(8 * source.Scale));

        var layout = new NoteLayout(spot.X, spot.Y, spot.Width, spot.Height, source.PinOpacity);
        AddNote(new StoredNote(NoteStore.CreateId(), source.Text, layout), activate: true, BoardMode ? null : _noteStore);
    }

    /// <summary>Reopens the notes that were open when PinPoint last exited (without taking focus).</summary>
    public void RestoreNotes()
    {
        foreach (var note in _noteStore.LoadAll()) AddNote(note, activate: false, _noteStore);
    }

    // ---- Boards -------------------------------------------------------------------------------

    /// <summary>True while a board is open: new notes then belong to the board rather than the notes folder.</summary>
    public bool BoardMode { get; set; }

    public IReadOnlyList<ImagePinWindow> Snips => _snips;

    public IReadOnlyList<NotePinWindow> Notes => _notes;

    public bool HasNotes => _notes.Count > 0;

    /// <summary>Other apps' pinned windows (never part of a board; a board image can show a snapshot of them).</summary>
    public IReadOnlyList<PinnedWindow> PinnedWindows => _windows.Pins;

    /// <summary>Re-pins a snip from a board where it was, with its zoom, opacity and drawing.</summary>
    public void RestoreSnip(CapturedFrame frame, Core.Boards.BoardSnip saved)
    {
        var window = new ImagePinWindow(frame, _storage, _ocr, _settings, saved.Id);
        window.Closed += (_, _) => OnSnipClosed(window);
        _snips.Add(window);
        Items.Add(new SnipPinItem(window));
        var b = saved.Bounds;
        window.ShowAt(new Core.Geometry.PixelRect(b.X, b.Y, b.Width, b.Height));
        window.RestoreBoardState(saved.ZoomStep, saved.Opacity, saved.Strokes, saved.Locked);
        OnStateChanged();
    }

    /// <summary>Re-opens a note from a board (it belongs to the board, not the notes folder).</summary>
    public void RestoreBoardNote(Core.Boards.BoardNote saved)
    {
        var b = saved.Bounds;
        var layout = new NoteLayout(b.X, b.Y, b.Width, b.Height, saved.Opacity, saved.ExportPath);
        AddNote(new StoredNote(saved.Id, saved.Text, layout), activate: false, store: null);
    }

    /// <summary>
    /// Closes every snip and note (used when switching boards). Loose notes keep their text in
    /// notes\closed; board notes were already saved with their board.
    /// </summary>
    public void CloseSnipsAndNotes()
    {
        foreach (var snip in _snips.ToArray()) snip.Close();
        foreach (var note in _notes.ToArray()) note.CloseNote();
    }

    private void AddNote(StoredNote note, bool activate, NoteStore? store)
    {
        var window = new NotePinWindow(note, store);
        window.Closed += (_, _) => OnNoteClosed(window);
        window.NewNoteRequested += (_, _) => CreateNoteBeside(window);
        window.DuplicateRequested += (_, _) => DuplicateNote(window);
        window.ClickThroughChanged += (_, _) => OnStateChanged();
        window.ClickThroughAllRequested += (_, enable) => SetClickThroughAll(enable);
        window.IsAllClickThrough = () => IsClickThrough;
        _notes.Add(window);
        Items.Add(new NotePinItem(window));
        window.ShowAt(note.Layout, activate);
        if (IsHidden) window.SetHidden(true);
        OnStateChanged();
    }

    private void OnNoteClosed(NotePinWindow window)
    {
        _notes.Remove(window);
        if (Items.OfType<NotePinItem>().FirstOrDefault(i => i.Window == window) is { } item) Items.Remove(item);
        OnStateChanged();
    }

    // ---- Global toggles -----------------------------------------------------------------------

    public void ToggleHidden()
    {
        if (!HasPins)
        {
            _notifier.Show("", "Nothing pinned yet");
            return;
        }

        IsHidden = !IsHidden;
        _windows.SetHidden(IsHidden);
        foreach (var surface in Surfaces) surface.SetHidden(IsHidden);
        _notifier.Show(IsHidden ? "" : "", IsHidden ? "Pins hidden" : "Pins shown");
        OnStateChanged();
    }

    public void ToggleClickThrough()
    {
        if (!HasPins)
        {
            _notifier.Show("", "Nothing pinned yet");
            return;
        }

        SetClickThroughAll(!IsClickThrough);
    }

    /// <summary>Click-through on or off for every pin (global toggle, or a note's menu/badge).</summary>
    public void SetClickThroughAll(bool enable)
    {
        foreach (var surface in Surfaces) surface.IsClickThrough = enable;
        foreach (var pin in _windows.Pins) _windows.SetClickThrough(pin, enable);

        _notifier.Show("", enable ? "Click-through on" : "Click-through off",
            enable ? "Clicks pass through pins to whatever is underneath." : "Pins can be moved and clicked again.");
        OnStateChanged();
    }

    /// <summary>
    /// Fades the pin under the mouse one step (direction +1 = more opaque, -1 = more transparent),
    /// or every pin when the mouse isn't over one. Driven by global shortcuts, so it works inside games
    /// and on pinned apps like Chrome that use Ctrl+scroll themselves.
    /// </summary>
    public void NudgeOpacity(int direction)
    {
        if (!HasPins)
        {
            _notifier.Show("", "Nothing pinned yet");
            return;
        }

        // Hit-test by bounds rather than WindowFromPoint, which skips click-through pins.
        Win32.GetCursorPos(out var cursor);
        var surface = Surfaces.LastOrDefault(s => s.ScreenBounds.Contains(cursor.X, cursor.Y));
        var window = surface is null
            ? _windows.Pins.LastOrDefault(p => WindowEnumerator.GetBounds(p.Handle).Contains(cursor.X, cursor.Y))
            : null;

        if (surface is not null)
        {
            surface.NudgeOpacity(direction);
            // Snips show their own badge; notes have nowhere to show one.
            if (surface is NotePinWindow) _notifier.Show("", $"Opacity {surface.PinOpacity:P0}", "Note");
        }
        else if (window is not null)
        {
            if (!_windows.SetOpacity(window, window.Opacity + direction * 0.1))
            {
                _notifier.Show("", "This window can't be faded", window.Title);
                return;
            }

            _notifier.Show("", $"Opacity {window.Opacity:P0}", window.Title);
        }
        else
        {
            foreach (var s in Surfaces) s.PinOpacity += direction * 0.1;
            foreach (var p in _windows.Pins) _windows.SetOpacity(p, p.Opacity + direction * 0.1);
            double shown = Surfaces.LastOrDefault()?.PinOpacity ?? _windows.Pins[^1].Opacity;
            _notifier.Show("", $"All pins · opacity {shown:P0}");
        }

        foreach (var item in Items) item.Refresh();
    }

    /// <summary>
    /// Stops pins being pushed back to the top until disposed — otherwise the periodic re-raise
    /// would pop live pins over the snip overlay mid-selection.
    /// </summary>
    public IDisposable SuspendRaising()
    {
        _raiseSuspensions++;
        return new Scope(() =>
        {
            _raiseSuspensions--;
            RaiseAll();
        });
    }

    /// <summary>
    /// Shows faded pins fully opaque until disposed, so a screen capture of a faded guide comes out
    /// clean instead of blended with whatever is behind it. Their opacity settings are untouched.
    /// </summary>
    public IDisposable ShowSolidForCapture()
    {
        int changed = _windows.SetTemporarilyOpaque(true);
        foreach (var surface in Surfaces)
        {
            if (!IsHidden && surface.SetTemporarilyOpaque(true)) changed++;
        }

        if (changed > 0)
        {
            // Let DWM compose a frame with the new alpha before anyone reads the screen.
            Win32.DwmFlush();
            Win32.DwmFlush();
        }

        return new Scope(() =>
        {
            if (changed == 0) return;
            _windows.SetTemporarilyOpaque(false);
            foreach (var surface in Surfaces) surface.SetTemporarilyOpaque(false);
        });
    }

    /// <summary>Puts every external window back exactly as it was. Safe to call more than once.</summary>
    public void RestoreExternalWindows() => _windows.UnpinAll();

    public void Dispose()
    {
        _maintenance.Stop();
        _raiseAgain.Stop();
        _foreground.Dispose();
        RestoreExternalWindows();
        CloseAllSnips();
        foreach (var note in _notes.ToArray()) note.Shutdown(); // saved, and back next launch
    }

    private void ShowAll()
    {
        if (!IsHidden) return;
        IsHidden = false;
        _windows.SetHidden(false);
        foreach (var surface in Surfaces) surface.SetHidden(false);
    }

    private void RaiseAll()
    {
        // Re-ordering windows closes open menus and can cover a note being typed in, so hold off then.
        if (IsHidden || _raiseSuspensions > 0 || Surfaces.Any(s => s.IsInteracting)) return;

        // Only when something from another app actually covers a pin: re-raising pins that are already on top
        // makes them (and the video or game under them) recompose every time, which shows as flicker.
        bool covered = _windows.AnyCovered() || Surfaces.Any(s => WindowEnumerator.IsCoveredByAnotherApp(s.Handle));
        if (!covered) return;

        _windows.RaiseAll();
        foreach (var surface in Surfaces) surface.RaiseTopmost();
    }

    private void Maintain()
    {
        if (_windows.Prune())
        {
            foreach (var item in Items) item.Refresh();
        }

        RaiseAll();
    }

    private void OnStateChanged() => StateChanged?.Invoke(this, EventArgs.Empty);
}
