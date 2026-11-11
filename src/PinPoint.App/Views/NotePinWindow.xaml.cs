using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using PinPoint.Core.Geometry;
using PinPoint.Core.Interop;
using PinPoint.Core.Notes;
using PinPoint.Core.Windows;
using PinPoint.Helpers;
using Windows.Graphics;

namespace PinPoint.Views;

/// <summary>
/// A pinned scratch-pad note: plain text, always on top, saved as you type and restored on restart.
/// Unlike snips it takes keyboard focus when clicked (you need focus to type), but never on its own.
/// </summary>
public sealed partial class NotePinWindow : Window, IPinSurface
{
    public const int DefaultWidth = 320, DefaultHeight = 260; // DIPs
    private const int MinWidth = 180, MinHeight = 110;

    private readonly nint _hwnd;
    /// <summary>Loose notes save to the notes folder; notes on a board have no store (the board saves them).</summary>
    private NoteStore? _store;
    private readonly WindowDragger _drag;
    private readonly DispatcherQueueTimer _saveTimer;
    private readonly DispatcherQueueTimer _layoutTimer;
    private readonly DispatcherQueueTimer _headerTimer;
    private double _opacity;
    private bool _clickThrough;
    private bool _shown;
    private string? _exportPath;

    public NotePinWindow(StoredNote note, NoteStore? store)
    {
        InitializeComponent();
        Id = note.Id;
        _store = store;
        _hwnd = this.GetHandle();
        _drag = new WindowDragger(AppWindow);
        _opacity = Math.Clamp(note.Layout?.Opacity ?? 1.0, 0.1, 1.0);
        _exportPath = note.Layout?.ExportPath;

        var presenter = WindowHelpers.MakeToolSurface(AppWindow, alwaysOnTop: true);
        presenter.IsResizable = true;
        double scale = WindowHelpers.GetScale(_hwnd);
        presenter.PreferredMinimumWidth = (int)(MinWidth * scale);
        presenter.PreferredMinimumHeight = (int)(MinHeight * scale);

        Editor.Text = note.Text;

        _saveTimer = DispatcherQueue.CreateTimer();
        _saveTimer.IsRepeating = false;
        _saveTimer.Interval = TimeSpan.FromMilliseconds(500);
        _saveTimer.Tick += (_, _) => SaveText();

        _layoutTimer = DispatcherQueue.CreateTimer();
        _layoutTimer.IsRepeating = false;
        _layoutTimer.Interval = TimeSpan.FromMilliseconds(600);
        _layoutTimer.Tick += (_, _) => SaveLayout();

        _headerTimer = DispatcherQueue.CreateTimer();
        _headerTimer.IsRepeating = false;
        _headerTimer.Interval = TimeSpan.FromSeconds(1.5);
        _headerTimer.Tick += (_, _) => UpdateHeader();

        AppWindow.Changed += (_, e) =>
        {
            if (!_shown || !(e.DidPositionChange || e.DidSizeChange)) return;
            RestartTimer(_layoutTimer);
            if (_badge?.IsShown == true) ShowBadge(); // keep it on the header
        };
        Closed += (_, _) => _badge?.Close();

        UpdateHeader();
    }

    /// <summary>The .txt file this note was saved to with Save/Save as, if any.</summary>
    public string? ExportPath => _exportPath;

    /// <summary>Raised when the note's title or opacity changes (for the Pins list).</summary>
    public event EventHandler? Changed;

    public string Id { get; }

    public string Text => Editor.Text;

    public nint Handle => _hwnd;

    public PixelRect ScreenBounds => WindowHelpers.GetClientScreenRect(_hwnd, AppWindow);

    /// <summary>Being typed in: re-ordering windows now could cover it or disturb input.</summary>
    public bool IsInteracting => Win32.GetForegroundWindow() == _hwnd;

    public double PinOpacity
    {
        get => _opacity;
        set
        {
            _opacity = Math.Clamp(Math.Round(value, 2), 0.1, 1.0);
            ApplyLayering();
            if (_badge?.IsShown == true) ShowBadge(); // fades with the note
            RestartTimer(_layoutTimer);
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    public bool IsClickThrough
    {
        get => _clickThrough;
        set
        {
            if (_clickThrough == value) return;
            _clickThrough = value;
            WindowStyler.SetExStyleFlag(_hwnd, Win32.WS_EX_TRANSPARENT, value);
            ApplyLayering();
            if (value && !_hidden) ShowBadge();
            else _badge?.HideBadge();
        }
    }

    // ---- Click-through badge -------------------------------------------------------------------

    private ClickThroughBadge? _badge;
    private bool _hidden;

    /// <summary>The note's own click-through changed from its button, menu or badge (PinManager refreshes the UI).</summary>
    public event EventHandler? ClickThroughChanged;

    /// <summary>Turn click-through on/off for every pin (header menu or badge).</summary>
    public event EventHandler<bool>? ClickThroughAllRequested;

    /// <summary>Whether every pin is click-through, for the header menu's "all pins" tick. Set by PinManager.</summary>
    public Func<bool>? IsAllClickThrough { get; set; }

    /// <summary>Puts the badge over the header's cursor button (third from the right), where the user clicked it.</summary>
    private void ShowBadge()
    {
        if (_badge is null)
        {
            _badge = new ClickThroughBadge();
            _badge.TurnOffRequested += (_, _) => SetOwnClickThrough(false);
            _badge.TurnOffAllRequested += (_, _) => ClickThroughAllRequested?.Invoke(this, false);
        }

        var client = ScreenBounds;
        double scale = Scale;
        int width = (int)Math.Round(34 * scale), height = (int)Math.Round(28 * scale);
        _badge.ShowAt(new PixelRect(client.Right - 3 * width, client.Y, width, height), _opacity);
    }

    private void SetOwnClickThrough(bool enabled)
    {
        IsClickThrough = enabled;
        ClickThroughChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>The header's cursor button: click-through for every pin (the menu has "this note" only).</summary>
    private void ClickThroughButton_Click(object sender, RoutedEventArgs e) => ClickThroughAllRequested?.Invoke(this, true);

    private void ClickThroughThisNote_Click(object sender, RoutedEventArgs e) => SetOwnClickThrough(true);

    private void ClickThroughAll_Click(object sender, RoutedEventArgs e) =>
        ClickThroughAllRequested?.Invoke(this, ClickThroughAllItem.IsChecked);

    /// <summary>Shows the note at its saved spot (if still on a monitor) or near the mouse.</summary>
    public void ShowAt(NoteLayout? layout, bool activate)
    {
        AppWindow.MoveAndResize(IsOnScreen(layout) ? new RectInt32(layout!.X, layout.Y, layout.Width, layout.Height) : DefaultBounds());
        // Fade before showing, so a faded note never appears at full opacity first.
        ApplyLayering();
        AppWindow.Show(activateWindow: activate);
        WindowStyler.RaiseTopmost(_hwnd);
        ApplyLayering();
        _shown = true;

        if (activate)
        {
            Win32.SetForegroundWindow(_hwnd);
            Activate();
            Editor.Focus(FocusState.Programmatic);
            Editor.SelectionStart = Editor.Text.Length;
        }

        // Activating/first render can drop the layered alpha again; re-apply once it has settled.
        DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, ApplyLayering);
        if (!Root.IsLoaded) Root.Loaded += (_, _) => ApplyLayering();
    }

    public void NudgeOpacity(int direction) => PinOpacity += direction * 0.1;

    public void SetHidden(bool hidden)
    {
        _hidden = hidden;
        if (hidden)
        {
            AppWindow.Hide();
            _badge?.HideBadge();
        }
        else
        {
            AppWindow.Show(activateWindow: false);
            if (_clickThrough) ShowBadge();
        }
    }

    public void RaiseTopmost()
    {
        WindowStyler.RaiseTopmost(_hwnd);
        _badge?.RaiseTopmost(); // stays above its note
    }

    /// <summary>For a screen capture: solid, and without the click-through badge in the shot.</summary>
    public bool SetTemporarilyOpaque(bool opaque)
    {
        bool changed = false;
        if (_clickThrough && !_hidden)
        {
            if (opaque) _badge?.HideBadge();
            else ShowBadge();
            changed = true;
        }

        if (_opacity < 0.999)
        {
            if (opaque) WindowStyler.SetAlpha(_hwnd, 255);
            else ApplyLayering();
            changed = true;
        }

        return changed;
    }

    /// <summary>The user closed the note: keep its text in notes\closed (or drop it if empty).</summary>
    public void CloseNote()
    {
        _saveTimer.Stop();
        _layoutTimer.Stop();
        _headerTimer.Stop();
        _store?.SaveText(Id, Editor.Text);
        _store?.Close(Id);
        Close();
    }

    /// <summary>PinPoint is exiting: save everything so the note comes back next time. Empty notes are dropped.</summary>
    public void Shutdown()
    {
        _saveTimer.Stop();
        _layoutTimer.Stop();
        _headerTimer.Stop();
        if (string.IsNullOrWhiteSpace(Editor.Text))
        {
            _store?.SaveText(Id, string.Empty);
            _store?.Close(Id);
        }
        else
        {
            SaveText();
            SaveLayout();
        }

        Close();
    }

    // ---- Saving ------------------------------------------------------------------------------

    private void Editor_TextChanged(object sender, TextChangedEventArgs e) => RestartTimer(_saveTimer);

    private void SaveText()
    {
        _store?.SaveText(Id, Editor.Text);
        Changed?.Invoke(this, EventArgs.Empty); // title in the Pins list follows the first line
    }

    private void SaveLayout()
    {
        var pos = AppWindow.Position;
        var size = AppWindow.Size;
        _store?.SaveLayout(Id, new NoteLayout(pos.X, pos.Y, size.Width, size.Height, _opacity, _exportPath));
    }

    // ---- Boards ------------------------------------------------------------------------------

    public bool IsOnBoard => _store is null;

    /// <summary>
    /// This loose note is now saved in a board: stop writing to the notes folder. Its old copy moves to
    /// notes\closed (kept as a backup) so it isn't also reopened as a loose note next launch.
    /// </summary>
    public void MoveToBoard()
    {
        if (_store is null) return;
        _saveTimer.Stop();
        _layoutTimer.Stop();
        _store.SaveText(Id, Editor.Text);
        _store.Close(Id);
        _store = null;
    }

    public Core.Boards.BoardNote ToBoardNote()
    {
        var pos = AppWindow.Position;
        var size = AppWindow.Size;
        return new Core.Boards.BoardNote(Id, Editor.Text, new Core.Boards.BoardRect(pos.X, pos.Y, size.Width, size.Height), _opacity)
        {
            ExportPath = _exportPath,
        };
    }

    // ---- Save to a file of the user's choosing (like Notepad) ---------------------------------

    private async void SaveButton_Click(object sender, RoutedEventArgs e) => await SaveToFileAsync(askForPlace: false);

    private async void Save_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        await SaveToFileAsync(askForPlace: false);
    }

    private async void SaveAs_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        await SaveToFileAsync(askForPlace: true);
    }

    /// <summary>
    /// First save (or Save as) asks where; after that, saves straight back to the same file.
    /// The note's own auto-save copy is unaffected.
    /// </summary>
    private async Task SaveToFileAsync(bool askForPlace)
    {
        try
        {
            string? path = askForPlace || _exportPath is null || !Directory.Exists(Path.GetDirectoryName(_exportPath))
                ? await PickFileAsync()
                : _exportPath;
            if (path is null) return; // cancelled

            await File.WriteAllTextAsync(path, Editor.Text);
            _exportPath = path;
            SaveLayout();
            Changed?.Invoke(this, EventArgs.Empty);
            FlashHeader("Saved");
        }
        catch (Exception ex)
        {
            Core.Diagnostics.Log.Error("Could not save note to file", ex);
            FlashHeader("Couldn't save");
        }
    }

    private async Task<string?> PickFileAsync()
    {
        var picker = new Windows.Storage.Pickers.FileSavePicker
        {
            SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.DocumentsLibrary,
            SuggestedFileName = _exportPath is not null ? Path.GetFileNameWithoutExtension(_exportPath) : SuggestedName(),
        };
        picker.FileTypeChoices.Add("Text file", [".txt"]);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, _hwnd);

        Windows.Storage.StorageFile? file;
        using (AppHost.Current.Pins.LowerForDialog()) file = await picker.PickSaveFileAsync(); // dialog in front of pins
        return file?.Path;
    }

    /// <summary>The note's first line, made safe for a file name (like Notepad's untitled → first line).</summary>
    private string SuggestedName()
    {
        string first = Editor.Text.Split('\r', '\n').FirstOrDefault(l => l.Trim().Length > 0)?.Trim() ?? string.Empty;
        string safe = string.Concat(first.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? ' ' : c)).Trim();
        return safe.Length == 0 ? "Note" : safe.Length > 50 ? safe[..50].TrimEnd() : safe;
    }

    private void UpdateHeader()
    {
        HeaderText.Text = _exportPath is null ? "Note" : Path.GetFileName(_exportPath);
        ToolTipService.SetToolTip(HeaderText, _exportPath);
    }

    private void FlashHeader(string message)
    {
        HeaderText.Text = _exportPath is null ? message : $"{message} · {Path.GetFileName(_exportPath)}";
        RestartTimer(_headerTimer);
    }

    private static void RestartTimer(DispatcherQueueTimer timer)
    {
        timer.Stop();
        timer.Start();
    }

    // ---- Header: drag + close ----------------------------------------------------------------

    private void Header_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (!e.GetCurrentPoint(Header).Properties.IsLeftButtonPressed) return;
        _drag.Begin(Header, e.Pointer);
        e.Handled = true;
    }

    private void Header_PointerMoved(object sender, PointerRoutedEventArgs e) => _drag.Update();

    private void Header_PointerReleased(object sender, PointerRoutedEventArgs e) => _drag.End(Header, e.Pointer);

    private void Header_PointerCaptureLost(object sender, PointerRoutedEventArgs e) => _drag.Cancel();

    private void CloseButton_Click(object sender, RoutedEventArgs e) => CloseNote();

    /// <summary>The + button: PinManager opens an empty note beside this one.</summary>
    public event EventHandler? NewNoteRequested;

    private void NewNoteButton_Click(object sender, RoutedEventArgs e) => NewNoteRequested?.Invoke(this, EventArgs.Empty);

    /// <summary>Menu → Duplicate: PinManager opens a copy of this note (text, size, opacity) beside it.</summary>
    public event EventHandler? DuplicateRequested;

    private void Duplicate_Click(object sender, RoutedEventArgs e) => DuplicateRequested?.Invoke(this, EventArgs.Empty);

    private void Menu_Opening(object sender, object e)
    {
        foreach (var item in new[] { Opacity100, Opacity80, Opacity60, Opacity40, Opacity20 })
            item.IsChecked = Math.Abs(double.Parse((string)item.Tag, System.Globalization.CultureInfo.InvariantCulture) - _opacity) < 0.01;
        ClickThroughAllItem.IsChecked = IsAllClickThrough?.Invoke() ?? false;
    }

    private void Opacity_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string tag })
            PinOpacity = double.Parse(tag, System.Globalization.CultureInfo.InvariantCulture);
    }

    private async void SaveAsMenu_Click(object sender, RoutedEventArgs e) => await SaveToFileAsync(askForPlace: true);

    /// <summary>The whole window in screen pixels (what <see cref="ShowAt"/> takes back).</summary>
    public PixelRect WindowBounds => new(AppWindow.Position.X, AppWindow.Position.Y, AppWindow.Size.Width, AppWindow.Size.Height);

    public double Scale => WindowHelpers.GetScale(_hwnd);

    // ---- Helpers -----------------------------------------------------------------------------

    private void ApplyLayering()
    {
        if (_opacity < 0.999 || _clickThrough)
            WindowStyler.SetAlpha(_hwnd, (byte)Math.Round(_opacity * 255));
        else
            WindowStyler.ClearLayered(_hwnd);
    }

    private static bool IsOnScreen(NoteLayout? layout)
    {
        if (layout is null || layout.Width <= 0 || layout.Height <= 0) return false;
        var rect = new PixelRect(layout.X, layout.Y, layout.Width, layout.Height);
        // At least a decent chunk (incl. the header) must be on some monitor, or it can't be grabbed.
        return WindowEnumerator.GetMonitors().Exists(m => rect.Intersect(m) is { Width: >= 60, Height: >= 40 });
    }

    /// <summary>New notes open near the mouse, kept inside that monitor's work area.</summary>
    private RectInt32 DefaultBounds()
    {
        Win32.GetCursorPos(out var cursor);
        var (_, work) = WindowEnumerator.GetMonitorAt(cursor.X, cursor.Y);
        AppWindow.Move(new PointInt32(work.X + 20, work.Y + 20)); // pick up that monitor's DPI
        double scale = WindowHelpers.GetScale(_hwnd);
        int width = (int)(DefaultWidth * scale), height = (int)(DefaultHeight * scale);
        int x = Math.Clamp(cursor.X - width / 2, work.X, Math.Max(work.X, work.Right - width));
        int y = Math.Clamp(cursor.Y - 20, work.Y, Math.Max(work.Y, work.Bottom - height));
        return new RectInt32(x, y, width, height);
    }
}
