using System.Collections.ObjectModel;
using Microsoft.UI.Dispatching;
using PinPoint.Core;
using PinPoint.Core.Capture;
using PinPoint.Core.Geometry;
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

    /// <summary>"Hide all pins": everything, pinned windows included.</summary>
    public bool IsHidden => _windowsHidden && _notesHidden && _snipsHidden;

    // Each kind of pin is hidden on its own; "hide all" just hides all three. That way a new pin only brings
    // back its own kind (pinning a window or snipping something doesn't make hidden notes reappear).
    private bool _windowsHidden, _notesHidden, _snipsHidden;

    public bool AreNotesHidden => _notesHidden;

    public bool AreSnipsHidden => _snipsHidden;

    public bool AreWindowsHidden => _windowsHidden;

    public bool HasWindowPins => _windows.Pins.Count > 0;

    public bool HasNotesOpen => _notes.Count > 0;

    private bool IsSurfaceHidden(IPinSurface surface) => _singleHidden.Contains(surface) || surface switch
    {
        NotePinWindow => _notesHidden,
        TimerPinWindow => IsHidden,
        _ => _snipsHidden,
    };

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
    private IEnumerable<IPinSurface> Surfaces => _snips.Cast<IPinSurface>().Concat(_notes).Concat(_timers);

    // ---- Timers -------------------------------------------------------------------------------

    private readonly List<TimerPinWindow> _timers = [];

    /// <summary>Pins a small stopwatch / countdown near the mouse. Timers hide only with "hide all".</summary>
    public void CreateTimer()
    {
        EndPeekNow();
        var window = new TimerPinWindow();
        window.Closed += (_, _) =>
        {
            _timers.Remove(window);
            _singleHidden.Remove(window);
            ForgetLayout(window);
            if (Items.OfType<TimerPinItem>().FirstOrDefault(i => i.Window == window) is { } item) Items.Remove(item);
            OnStateChanged();
        };
        window.Finished += (_, _) =>
        {
            if (!TimerPinWindow.IsSilent) _notifier.Show("", "Time's up", window.Title, force: true);
        };
        _timers.Add(window);
        Items.Add(new TimerPinItem(window));
        window.ShowNearCursor();
        if (IsHidden) window.SetHidden(true);
        Track(window);
        OnStateChanged();
    }

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

        ShowAll(windows: true);
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
        ShowAll(windows: true);
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
        ShowAll(snips: true);
        var window = new ImagePinWindow(frame, _storage, _ocr, _settings);
        WireSnip(window);
        _snips.Add(window);
        Items.Add(new SnipPinItem(window));
        window.ShowAt(frame.Bounds);
        Track(window);
        OnStateChanged();
        return window;
    }

    /// <summary>"Close all snips": each can be brought back with undo.</summary>
    public void CloseAllSnips()
    {
        foreach (var snip in _snips.ToArray()) snip.CloseByUser();
    }

    private void WireSnip(ImagePinWindow window)
    {
        window.Closed += (_, _) => OnSnipClosed(window);
        window.UserClosing += (_, _) => RememberClosed(window);
    }

    private void OnSnipClosed(ImagePinWindow window)
    {
        _snips.Remove(window);
        _singleHidden.Remove(window);
        ForgetLayout(window);
        if (Items.OfType<SnipPinItem>().FirstOrDefault(i => i.Window == window) is { } item) Items.Remove(item);
        OnStateChanged();
    }

    // ---- Notes --------------------------------------------------------------------------------

    /// <summary>Opens a new, empty note near the mouse and puts the cursor in it.</summary>
    public void CreateNote()
    {
        ShowAll(notes: true);
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
    public ImagePinWindow RestoreSnip(CapturedFrame frame, Core.Boards.BoardSnip saved)
    {
        var window = new ImagePinWindow(frame, _storage, _ocr, _settings, saved.Id);
        WireSnip(window);
        _snips.Add(window);
        Items.Add(new SnipPinItem(window));
        var b = saved.Bounds;
        // A snip saved off-screen (a zoom that went too far, a monitor that's gone) comes back where you can see it.
        var spot = ScreenFit.BringOnScreen(new PixelRect(b.X, b.Y, b.Width, b.Height), WindowEnumerator.GetMonitors(), minVisible: 120);
        window.ShowAt(spot);
        window.RestoreBoardState(saved.ZoomStep, saved.Opacity, saved.Strokes, saved.Locked);
        if (AreSnipsHidden) window.SetHidden(true);
        Track(window);
        OnStateChanged();
        return window;
    }

    // ---- Undo / redo (Ctrl+Alt+Z / Ctrl+Alt+Y): closing pins, and moving or resizing them ---------------

    private abstract record UndoEntry;

    private sealed record ClosedSnip(CapturedFrame Frame, Core.Boards.BoardSnip State, string? SavedPath) : UndoEntry;

    private sealed record ClosedNote(Core.Boards.BoardNote State) : UndoEntry;

    /// <summary>A pin that was moved/resized/zoomed: where it was before (or, on the redo side, after).</summary>
    private sealed record Moved(IPinSurface Surface, PinLayout Layout) : UndoEntry;

    /// <summary>Redo side of an undone close: the reopened pin, to close again.</summary>
    private sealed record Reopened(IPinSurface Surface) : UndoEntry;

    private const int UndoDepth = 50;
    private readonly List<UndoEntry> _undo = [];
    private readonly List<UndoEntry> _redo = [];
    private bool _replaying; // undo/redo is moving or closing things itself: don't record that as new history

    /// <summary>The Undo shortcut as text ("Ctrl+Alt+Z"), for the "closed" notice. Set by AppHost.</summary>
    public Func<string?>? UndoShortcut { get; set; }

    public bool CanUndoClose => _undo.Count > 0;

    public bool CanRedoClose => _redo.Count > 0;

    private void Record(UndoEntry entry)
    {
        _undo.Add(entry);
        if (_undo.Count > UndoDepth) _undo.RemoveAt(0);
        if (!_replaying) _redo.Clear(); // a fresh action starts a new future, like any undo
        OnStateChanged();
    }

    private void RememberClosed(ImagePinWindow snip)
    {
        var r = snip.ScreenBounds;
        var state = new Core.Boards.BoardSnip(snip.Id, new Core.Boards.BoardRect(r.X, r.Y, r.Width, r.Height),
            snip.CurrentZoomStep, snip.PinOpacity, snip.GetStrokePoints()) { Locked = snip.IsLocked };
        RememberClose(new ClosedSnip(snip.Frame, state, snip.SavedPath), "Snip closed");
    }

    private void RememberClosed(NotePinWindow note) => RememberClose(new ClosedNote(note.ToBoardNote()), "Note closed");

    private void RememberClose(UndoEntry entry, string what)
    {
        Record(entry);
        if (_replaying) return;
        string? key = UndoShortcut?.Invoke();
        _notifier.Show("\uE7A7", what, key is null ? "Undo it from the tray menu." : $"{key} brings it back.");
    }

    // Moves and resizes: one undo step per gesture. The first change after the pin has been still records where it
    // was; the gesture ends once it's been still for a moment (a drag, a burst of scroll-zoom, an edge resize).
    private readonly Dictionary<IPinSurface, PinLayout> _stableLayout = [];
    private readonly HashSet<IPinSurface> _changing = [];
    private DispatcherQueueTimer? _settle;

    /// <summary>Starts following a pin's moves for undo (call once it's in place on screen).</summary>
    private void Track(IPinSurface surface)
    {
        _stableLayout[surface] = surface.Layout;
        surface.LayoutChanged += (_, _) => OnLayoutChanged(surface);
    }

    private void OnLayoutChanged(IPinSurface surface)
    {
        // Peek slides pins aside and back by itself; undo/redo moves them too. Neither is a user's move.
        if (_replaying || IsPeeking || !_stableLayout.TryGetValue(surface, out var before)) return;
        if (_changing.Add(surface)) Record(new Moved(surface, before));

        _settle ??= CreateSettleTimer();
        _settle.Stop();
        _settle.Start();
    }

    private DispatcherQueueTimer CreateSettleTimer()
    {
        var timer = DispatcherQueue.GetForCurrentThread().CreateTimer();
        timer.IsRepeating = false;
        timer.Interval = TimeSpan.FromMilliseconds(450);
        timer.Tick += (_, _) =>
        {
            foreach (var surface in _changing) _stableLayout[surface] = surface.Layout;
            _changing.Clear();
        };
        return timer;
    }

    private void ForgetLayout(IPinSurface surface)
    {
        _stableLayout.Remove(surface);
        _changing.Remove(surface);
    }

    /// <summary>Undo: steps back one thing — a move/resize/zoom, or a close (the pin comes back where it was).</summary>
    public void UndoClose() => Step(_undo, _redo, undoing: true);

    /// <summary>Redo: steps forward again.</summary>
    public void RedoClose() => Step(_redo, _undo, undoing: false);

    private void Step(List<UndoEntry> from, List<UndoEntry> to, bool undoing)
    {
        EndPeekNow();
        _settle?.Stop();
        foreach (var surface in _changing) _stableLayout[surface] = surface.Layout;
        _changing.Clear();

        while (from.Count > 0)
        {
            var entry = from[^1];
            from.RemoveAt(from.Count - 1);
            if (TryApply(entry, to)) return;
        }

        _notifier.Show(undoing ? "\uE7A7" : "\uE7A6", undoing ? "Nothing to undo" : "Nothing to redo");
    }

    /// <summary>Applies one history entry, pushing its opposite onto <paramref name="opposite"/>. False if it no longer applies.</summary>
    private bool TryApply(UndoEntry entry, List<UndoEntry> opposite)
    {
        _replaying = true;
        try
        {
            switch (entry)
            {
                case Moved(var surface, var layout):
                    if (!IsOpen(surface)) return false;
                    opposite.Add(new Moved(surface, surface.Layout));
                    surface.ApplyLayout(layout);
                    _stableLayout[surface] = layout;
                    _notifier.Show("\uE7A7", "Moved back");
                    break;

                case ClosedSnip snip:
                {
                    ShowAll(snips: true);
                    var window = RestoreSnip(snip.Frame, snip.State);
                    window.SavedPath = snip.SavedPath;
                    opposite.Add(new Reopened(window));
                    _notifier.Show("\uE7A7", "Snip reopened");
                    break;
                }

                case ClosedNote(var note):
                {
                    ShowAll(notes: true);
                    var b = note.Bounds;
                    var layout = new NoteLayout(b.X, b.Y, b.Width, b.Height, note.Opacity, note.ExportPath);
                    var window = AddNote(new StoredNote(note.Id, note.Text, layout), activate: false, BoardMode ? null : _noteStore);
                    opposite.Add(new Reopened(window));
                    _notifier.Show("\uE7A7", "Note reopened");
                    break;
                }

                case Reopened(var surface):
                    if (!IsOpen(surface)) return false;
                    // Closing records the close on the undo side (we're replaying, so redo is kept).
                    if (surface is ImagePinWindow s) s.CloseByUser();
                    else if (surface is NotePinWindow n) n.CloseNoteByUser();
                    else return false;
                    break;
            }
        }
        finally
        {
            _replaying = false;
        }

        OnStateChanged();
        return true;
    }

    private bool IsOpen(IPinSurface pin) => pin switch
    {
        ImagePinWindow s => _snips.Contains(s),
        NotePinWindow n => _notes.Contains(n),
        TimerPinWindow t => _timers.Contains(t),
        _ => false,
    };

    // ---- Peek: slide every pin out to the screen edges and back ----------------------------------

    private const double PeekSeconds = 0.14; // snappy: it's a quick look, not a show

    private sealed class PeekMove(nint hwnd, PixelRect home, PixelRect away)
    {
        public nint Hwnd { get; } = hwnd;
        public PixelRect Home { get; } = home;
        public PixelRect Away { get; } = away;
        public PixelRect From { get; set; } = home;
        public PixelRect To { get; set; } = away;
    }

    private List<PeekMove>? _peek;          // set while pins are out (or sliding)
    private bool _peekReturning;

    // The slide is timed by its own thread with a precise clock, not WinUI's render tick: that tick nearly stops
    // while PinPoint sits behind a game, which made the slide crawl (and the way back never run).
    private int _peekGeneration; // bumped to cancel the running slide

    /// <summary>Pins are slid out to the edges (or on their way out or back).</summary>
    public bool IsPeeking => _peek is not null;

    /// <summary>
    /// Like "show desktop" for your pins: snips, notes and timers slide out past the nearest screen edge (a thin
    /// sliver stays visible) so you can work on what's under them; press again and they slide right back.
    /// </summary>
    public void TogglePeek()
    {
        if (_peek is null || _peekReturning) PeekOut();
        else PeekBack();
    }

    /// <summary>The Peek shortcut's key (virtual-key code), for telling a tap from a hold. Set by AppHost.</summary>
    public Func<int?>? PeekKey { get; set; }

    private const double HoldSeconds = 0.3;
    private DispatcherQueueTimer? _holdWatch;
    private int _holdKey;
    private readonly System.Diagnostics.Stopwatch _heldFor = new();

    /// <summary>
    /// The Peek shortcut. A tap slides the pins out and leaves them there until the next tap; holding it peeks
    /// only while held (like Aero Peek): let go and they snap back.
    /// </summary>
    public void PeekFromShortcut()
    {
        bool goingOut = _peek is null || _peekReturning;
        TogglePeek();
        if (!goingOut || _peek is null || PeekKey?.Invoke() is not { } key) return;

        _heldFor.Restart();
        _holdKey = key;
        _holdWatch ??= CreateHoldWatch();
        _holdWatch.Start();
    }

    private DispatcherQueueTimer CreateHoldWatch()
    {
        var timer = DispatcherQueue.GetForCurrentThread().CreateTimer();
        timer.Interval = TimeSpan.FromMilliseconds(25);
        timer.Tick += (t, _) =>
        {
            if (Win32.IsKeyDown(_holdKey)) return; // still held
            t.Stop();
            // Released after a proper hold: that was a peek, put them back. A quick tap leaves them out.
            if (_heldFor.Elapsed.TotalSeconds >= HoldSeconds && _peek is not null && !_peekReturning) PeekBack();
        };
        return timer;
    }

    private void PeekOut()
    {
        if (_peek is not null && _peekReturning)
        {
            // Pressed again while sliding back: turn round from where they are.
            foreach (var move in _peek)
            {
                move.From = CurrentRect(move);
                move.To = move.Away;
            }

            _peekReturning = false;
            StartPeekAnimation();
            OnStateChanged();
            return;
        }

        var monitors = WindowEnumerator.GetMonitors();
        var moves = new List<PeekMove>();
        foreach (var surface in Surfaces.Where(s => !IsSurfaceHidden(s)))
        {
            Win32.GetWindowRect(surface.Handle, out var r);
            var rect = PixelRect.FromRect(r);
            var (monitor, _) = WindowEnumerator.GetMonitorAt(rect.X + rect.Width / 2, rect.Y + rect.Height / 2);
            int sliver = (int)Math.Round(18 * WindowHelpers.GetScale(surface.Handle));
            moves.Add(new PeekMove(surface.Handle, rect, PeekLayout.Target(rect, monitor, monitors, sliver)));
        }

        if (moves.Count == 0)
        {
            _notifier.Show("", "No pins to move aside");
            return;
        }

        _peek = moves;
        _peekReturning = false;
        foreach (var note in _notes) note.HoldLayout = true; // don't save the "away" spot as the note's place
        StartPeekAnimation();
        OnStateChanged();
    }

    private void PeekBack()
    {
        if (_peek is null) return;
        foreach (var move in _peek)
        {
            move.From = CurrentRect(move);
            move.To = move.Home;
        }

        _peekReturning = true;
        StartPeekAnimation();
        OnStateChanged();
    }

    /// <summary>Puts peeked pins straight back (no animation): before saving a board, pinning something new, exiting.</summary>
    public void EndPeekNow()
    {
        if (_peek is null) return;
        Interlocked.Increment(ref _peekGeneration); // stop the slide first (frames run on this thread, so none can follow)
        Win32.MoveWindowsTogether([.. _peek.Where(m => Win32.IsWindow(m.Hwnd)).Select(m => (m.Hwnd, m.Home.X, m.Home.Y))]);
        FinishPeek();
    }

    private void FinishPeek()
    {
        Interlocked.Increment(ref _peekGeneration);
        _peek = null;
        _peekReturning = false;
        foreach (var note in _notes) note.HoldLayout = false;
        OnStateChanged();
    }

    private static PixelRect CurrentRect(PeekMove move)
    {
        if (!Win32.IsWindow(move.Hwnd)) return move.Home;
        Win32.GetWindowRect(move.Hwnd, out var r);
        return PixelRect.FromRect(r);
    }

    /// <summary>
    /// Slides every peek window from its From to its To over <see cref="PeekSeconds"/>, ~140 Hz, on a short-lived
    /// high-priority thread. A newer slide (or EndPeekNow) cancels this one mid-way.
    /// </summary>
    private void StartPeekAnimation()
    {
        if (_peek is null) return;
        int generation = Interlocked.Increment(ref _peekGeneration);
        var plan = _peek.Select(m => (m.Hwnd, m.From, m.To)).ToArray();
        bool returning = _peekReturning;
        var dispatcher = DispatcherQueue.GetForCurrentThread();

        var clock = System.Diagnostics.Stopwatch.StartNew();
        bool done = false;

        // One frame, on the UI thread (it owns the windows): where everything should be right now.
        void Frame()
        {
            if (_peekGeneration != generation || done) return;
            double t = PeekLayout.EaseOut(clock.Elapsed.TotalSeconds / PeekSeconds);
            Win32.MoveWindowsTogether([.. plan.Where(p => Win32.IsWindow(p.Hwnd))
                .Select(p => PeekLayout.Lerp(p.From, p.To, t) is var r ? (p.Hwnd, r.X, r.Y) : default)]);
            if (t < 1) return;
            done = true;
            if (returning) FinishPeek(); // back home: done
        }

        // The ticker only keeps time; frames run on the UI thread, which is idle behind a game, so they're prompt.
        var thread = new Thread(() =>
        {
            Win32.timeBeginPeriod(1); // make the short sleeps accurate
            try
            {
                while (!Volatile.Read(ref done) && Volatile.Read(ref _peekGeneration) == generation)
                {
                    dispatcher.TryEnqueue(DispatcherQueuePriority.High, Frame);
                    Thread.Sleep(7);
                }
            }
            finally
            {
                Win32.timeEndPeriod(1);
            }
        })
        {
            IsBackground = true,
            Priority = ThreadPriority.AboveNormal,
            Name = "PinPoint peek",
        };
        thread.Start();
    }

    // ---- Hide the pin under the mouse ------------------------------------------------------------

    /// <summary>Pins hidden one at a time (on top of the per-kind hiding).</summary>
    private readonly HashSet<IPinSurface> _singleHidden = [];

    /// <summary>
    /// Hides the snip, note or timer under the mouse. Pressed with the mouse over no pin, it brings back the
    /// ones hidden this way. "Hide all" off brings them back too.
    /// </summary>
    public void HidePinUnderMouse()
    {
        Win32.GetCursorPos(out var cursor);
        var visible = Surfaces.Where(s => !IsSurfaceHidden(s)).ToDictionary(s => s.Handle);
        // Topmost first, by real z-order (bounds alone can't tell overlapping pins apart).
        var target = WindowEnumerator.TopLevelInZOrder()
            .Select(h => visible.GetValueOrDefault(h))
            .FirstOrDefault(s => s is not null && s.ScreenBounds.Contains(cursor.X, cursor.Y));

        if (target is not null)
        {
            _singleHidden.Add(target);
            target.SetHidden(true);
            _notifier.Show("", "Pin hidden", "Press again with the mouse off your pins to bring hidden ones back.");
            OnStateChanged();
            return;
        }

        if (_singleHidden.Count == 0)
        {
            _notifier.Show("", "No pin under the mouse");
            return;
        }

        ShowSingles();
        _notifier.Show("", "Hidden pins shown");
    }

    private void ShowSingles()
    {
        var shown = _singleHidden.ToList();
        _singleHidden.Clear();
        foreach (var surface in shown) surface.SetHidden(IsSurfaceHidden(surface));
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
        EndPeekNow();
        foreach (var snip in _snips.ToArray()) snip.Close();
        foreach (var note in _notes.ToArray()) note.CloseNote();
    }

    private NotePinWindow AddNote(StoredNote note, bool activate, NoteStore? store)
    {
        var window = new NotePinWindow(note, store);
        window.Closed += (_, _) => OnNoteClosed(window);
        window.UserClosing += (_, _) => RememberClosed(window);
        window.NewNoteRequested += (_, _) => CreateNoteBeside(window);
        window.DuplicateRequested += (_, _) => DuplicateNote(window);
        window.HideNotesRequested += (_, _) => ToggleNotesHidden();
        window.ClickThroughChanged += (_, _) => OnStateChanged();
        window.ClickThroughAllRequested += (_, enable) => SetClickThroughAll(enable, notify: false); // the badge shows it
        window.IsAllClickThrough = () => IsClickThrough;
        _notes.Add(window);
        Items.Add(new NotePinItem(window));
        window.ShowAt(note.Layout, activate);
        if (AreNotesHidden) window.SetHidden(true);
        Track(window);
        OnStateChanged();
        return window;
    }

    private void OnNoteClosed(NotePinWindow window)
    {
        _notes.Remove(window);
        _singleHidden.Remove(window);
        ForgetLayout(window);
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

        // Showing them again brings everything back, including notes or snips hidden on their own.
        bool hide = !IsHidden;
        SetHiddenState(windows: hide, notes: hide, snips: hide);
        if (!hide && _singleHidden.Count > 0) ShowSingles(); // "show all" means all
        _notifier.Show(IsHidden ? "" : "", IsHidden ? "Pins hidden" : "Pins shown");
        OnStateChanged();
    }

    /// <summary>Hides every note (or brings them back), leaving snips and pinned windows alone.</summary>
    public void ToggleNotesHidden()
    {
        if (_notes.Count == 0)
        {
            _notifier.Show("", "No notes open");
            return;
        }

        bool hide = !_notesHidden;
        SetHiddenState(_windowsHidden, notes: hide, snips: _snipsHidden);
        _notifier.Show(hide ? "" : "", hide ? "Notes hidden" : "Notes shown",
            hide ? "Bring them back from the tray menu, the Pins page or your shortcut." : null);
    }

    /// <summary>Hides every snip (or brings them back), leaving notes and pinned windows alone.</summary>
    public void ToggleSnipsHidden()
    {
        if (_snips.Count == 0)
        {
            _notifier.Show("", "No snips pinned");
            return;
        }

        bool hide = !_snipsHidden;
        SetHiddenState(_windowsHidden, notes: _notesHidden, snips: hide);
        _notifier.Show(hide ? "" : "", hide ? "Snips hidden" : "Snips shown",
            hide ? "Bring them back from the tray menu, the Pins page or your shortcut." : null);
    }

    /// <summary>True when every open note is at (about) half opacity, for the tray tick.</summary>
    public bool AreNotesHalfFaded => _notes.Count > 0 && _notes.All(n => Math.Abs(n.PinOpacity - 0.5) < 0.01);

    /// <summary>Tray → every note to 50% opacity, or back to fully solid if they already are.</summary>
    public void ToggleNotesHalfFaded()
    {
        if (_notes.Count == 0)
        {
            _notifier.Show("", "No notes open");
            return;
        }

        double target = AreNotesHalfFaded ? 1.0 : 0.5;
        foreach (var note in _notes) note.PinOpacity = target;
        _notifier.Show("", target < 1 ? "Notes at 50% opacity" : "Notes fully solid");
    }

    /// <summary>Hides every pinned app window (or brings them back), leaving snips and notes alone.</summary>
    public void ToggleWindowsHidden()
    {
        if (_windows.Pins.Count == 0)
        {
            _notifier.Show("", "No windows pinned");
            return;
        }

        bool hide = !_windowsHidden;
        SetHiddenState(windows: hide, notes: _notesHidden, snips: _snipsHidden);
        _notifier.Show(hide ? "" : "", hide ? "Pinned windows hidden" : "Pinned windows shown",
            hide ? "Bring them back from the tray menu, the Pins page or your shortcut." : null);
    }

    // ---- Finding buried pins, and fading everything -----------------------------------------------

    private int _cycle = -1;

    /// <summary>Brings the next (+1) or previous (-1) visible pin to the front, cycling, like Alt+Tab just for pins.</summary>
    public void CyclePins(int direction)
    {
        var all = Surfaces.Where(s => !IsSurfaceHidden(s)).Select(s => s.Handle)
            .Concat(_windows.Pins.Select(p => p.Handle)).Where(Win32.IsWindowVisible).ToList();
        if (all.Count == 0)
        {
            _notifier.Show("", "No pins showing");
            return;
        }

        _cycle = ((_cycle + direction) % all.Count + all.Count) % all.Count;
        WindowStyler.RaiseTopmost(all[_cycle]);
        Raised?.Invoke();
        _notifier.Show("", $"Pin {_cycle + 1} of {all.Count}", Win32.GetWindowText(all[_cycle]));
    }

    /// <summary>
    /// Lifts the app window under the mouse on top of everything (pinned, with the coloured highlight border so you
    /// can tell it's lifted); pressed again over it, puts it back. Never a snip, note or other PinPoint window.
    /// </summary>
    public void ToggleLiftUnderMouse()
    {
        Win32.GetCursorPos(out var cursor);
        nint hwnd = Win32.WindowFromPoint(cursor);
        nint root = hwnd != 0 ? Win32.GetAncestor(hwnd, Win32.GA_ROOT) : 0;
        if (root == 0 || Win32.GetProcessId(root) == Environment.ProcessId || WindowEnumerator.IsShell(root))
        {
            _notifier.Show("", "Point at an app window to lift it");
            return;
        }

        bool wasLifted = IsWindowPinned(root);
        if (wasLifted) UnpinWindow(root);
        else PinWindow(root);
        _notifier.Show("", wasLifted ? "Put back" : "Lifted to the front",
            wasLifted ? null : "Press the shortcut again over it to put it back.");
    }

    /// <summary>Lifts every pin back to the front: for pins buried under other pins or windows.</summary>
    public void BringAllToFront()
    {
        if (!HasPins)
        {
            _notifier.Show("", "Nothing pinned yet");
            return;
        }

        _windows.RaiseAll();
        foreach (var surface in Surfaces) surface.RaiseTopmost();
        Raised?.Invoke();
        _notifier.Show("", "Pins brought to front");
    }

    /// <summary>Fades (-1) or unfades (+1) every pin one step at once, wherever the mouse is.</summary>
    public void NudgeAllOpacity(int direction)
    {
        if (!HasPins)
        {
            _notifier.Show("", "Nothing pinned yet");
            return;
        }

        foreach (var s in Surfaces) s.PinOpacity += direction * 0.1;
        foreach (var p in _windows.Pins) _windows.SetOpacity(p, p.Opacity + direction * 0.1);
        foreach (var item in Items) item.Refresh();
        double shown = Surfaces.FirstOrDefault()?.PinOpacity ?? _windows.Pins.FirstOrDefault()?.Opacity ?? 1;
        _notifier.Show("", $"All pins · opacity {shown:P0}");
    }

    /// <summary>Applies a new hidden state, showing/hiding only the kinds of pin whose visibility changed.</summary>
    private void SetHiddenState(bool windows, bool notes, bool snips)
    {
        EndPeekNow(); // hidden pins come back where they live, never stranded at the edge
        bool windowsWere = _windowsHidden, notesWere = _notesHidden, snipsWere = _snipsHidden, allWere = IsHidden;
        _windowsHidden = windows;
        _notesHidden = notes;
        _snipsHidden = snips;

        if (windowsWere != windows) _windows.SetHidden(windows);
        // IsSurfaceHidden: a pin hidden on its own (hide pin under mouse) stays hidden when its kind comes back.
        if (notesWere != notes) foreach (var note in _notes) note.SetHidden(IsSurfaceHidden(note));
        if (snipsWere != snips) foreach (var snip in _snips) snip.SetHidden(IsSurfaceHidden(snip));
        if (allWere != IsHidden) foreach (var timer in _timers) timer.SetHidden(IsSurfaceHidden(timer));
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

    /// <summary>
    /// Click-through on or off for every pin (global toggle, or a note's button/menu/badge). Notes ask for no
    /// toast: the badge on each note already shows it.
    /// </summary>
    public void SetClickThroughAll(bool enable, bool notify = true)
    {
        foreach (var surface in Surfaces) surface.IsClickThrough = enable;
        foreach (var pin in _windows.Pins) _windows.SetClickThrough(pin, enable);

        if (!notify)
        {
            OnStateChanged();
            return;
        }

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
    /// Wrap a Save/Open dialog in this so it comes up in front of pins and games. The pins stay on top (lowering
    /// them let a borderless game cover the snip you were saving); instead the dialog itself is made always on top
    /// and brought forward as soon as it appears. Re-raising pins is held off while it's open so they don't jump
    /// back over it.
    /// </summary>
    public IDisposable DialogInFront()
    {
        _raiseSuspensions++;
        var stop = new CancellationTokenSource();
        _ = BringDialogForwardAsync(stop.Token);
        return new Scope(() =>
        {
            stop.Cancel();
            _raiseSuspensions--;
        });
    }

    /// <summary>
    /// Watches (off the UI thread, which is busy running the dialog) for our process's dialog window
    /// (class #32770) to appear, then puts it on top and gives it focus.
    /// </summary>
    private static async Task BringDialogForwardAsync(CancellationToken cancel)
    {
        await Task.Run(async () =>
        {
            uint pid = (uint)Environment.ProcessId;
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (!cancel.IsCancellationRequested && DateTime.UtcNow < deadline)
            {
                foreach (nint hwnd in WindowEnumerator.TopLevelInZOrder())
                {
                    if (!Win32.IsWindowVisible(hwnd) || Win32.GetProcessId(hwnd) != pid || Win32.GetClassName(hwnd) != "#32770") continue;
                    WindowStyler.SetTopmost(hwnd, true);
                    // Focus too, so you can type the file name straight away (a plain SetForegroundWindow is
                    // often refused here, leaving the keyboard on the page you snipped). Retried until it sticks.
                    if (Win32.ForceForeground(hwnd)) return;
                    break;
                }

                try
                {
                    await Task.Delay(40, cancel);
                }
                catch (TaskCanceledException)
                {
                    return;
                }
            }
        });
    }

    /// <summary>
    /// Shows faded pins fully opaque until disposed, so a screen capture of a faded guide comes out
    /// clean instead of blended with whatever is behind it. Their opacity settings are untouched.
    /// </summary>
    /// <summary>Called with true just before a screen capture and false after; returns whether it hid anything.</summary>
    public Func<bool, bool>? CaptureHook { get; set; }

    /// <summary>Other always-on-top PinPoint windows (quick slots) that should be kept above other apps too.</summary>
    public Func<IEnumerable<nint>>? ExtraTopmost { get; set; }

    /// <summary>Pins were just pushed back on top (other floating PinPoint UI follows them up).</summary>
    public event Action? Raised;

    /// <summary>For other parts of the app whose state the tray/Pins page show (quick slots on/off).</summary>
    public void NotifyStateChanged() => OnStateChanged();

    public IDisposable ShowSolidForCapture()
    {
        int changed = _windows.SetTemporarilyOpaque(true);
        foreach (var surface in Surfaces)
        {
            if (!IsSurfaceHidden(surface) && surface.SetTemporarilyOpaque(true)) changed++;
        }

        bool hooked = CaptureHook?.Invoke(true) == true; // e.g. quick slots step out of the shot
        if (changed > 0 || hooked)
        {
            // Let DWM compose a frame with the new alpha before anyone reads the screen.
            Win32.DwmFlush();
            Win32.DwmFlush();
        }

        return new Scope(() =>
        {
            if (hooked) CaptureHook?.Invoke(false);
            if (changed == 0) return;
            _windows.SetTemporarilyOpaque(false);
            foreach (var surface in Surfaces) surface.SetTemporarilyOpaque(false);
        });
    }

    /// <summary>Puts every external window back exactly as it was. Safe to call more than once.</summary>
    public void RestoreExternalWindows() => _windows.UnpinAll();

    public void Dispose()
    {
        EndPeekNow(); // notes save their real spot, not the edge
        _holdWatch?.Stop();
        _maintenance.Stop();
        _raiseAgain.Stop();
        _foreground.Dispose();
        RestoreExternalWindows();
        CloseAllSnips();
        foreach (var note in _notes.ToArray()) note.Shutdown(); // saved, and back next launch
        foreach (var timer in _timers.ToArray()) timer.CloseTimer();
    }

    /// <summary>
    /// A new pin is about to appear: un-hide its own kind (so it isn't the only one showing). Other kinds stay
    /// hidden, e.g. pinning a window or snipping doesn't bring hidden notes back.
    /// </summary>
    private void ShowAll(bool windows = false, bool notes = false, bool snips = false)
    {
        EndPeekNow(); // a new pin: everything comes back from the edges first
        if (!(windows && _windowsHidden) && !(notes && _notesHidden) && !(snips && _snipsHidden)) return;
        SetHiddenState(windows: !windows && _windowsHidden, notes: !notes && _notesHidden, snips: !snips && _snipsHidden);
    }

    private void RaiseAll()
    {
        // Re-ordering windows closes open menus and can cover a note being typed in, so hold off then.
        if (IsHidden || _raiseSuspensions > 0 || Surfaces.Any(s => s.IsInteracting)) return;

        // You're using a PinPoint window (the main window, a dialog): it comes first; don't push pins over it.
        nint foreground = Win32.GetForegroundWindow();
        if (foreground != 0 && Win32.GetProcessId(foreground) == Environment.ProcessId) return;

        // Only when something from another app actually covers a pin: re-raising pins that are already on top
        // makes them (and the video or game under them) recompose every time, which shows as flicker.
        // Windows you've pinned yourself don't count as covering a snip or note.
        var pinnedWindows = _windows.Pins.Select(p => p.Handle).ToHashSet();
        bool covered = _windows.AnyCovered() ||
            Surfaces.Any(s => !IsSurfaceHidden(s) && WindowEnumerator.IsCoveredByAnotherApp(s.Handle, pinnedWindows)) ||
            (ExtraTopmost?.Invoke() ?? []).Any(h => WindowEnumerator.IsCoveredByAnotherApp(h, pinnedWindows));
        if (!covered) return;

        _windows.RaiseAll();
        foreach (var surface in Surfaces) surface.RaiseTopmost();
        Raised?.Invoke();
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
