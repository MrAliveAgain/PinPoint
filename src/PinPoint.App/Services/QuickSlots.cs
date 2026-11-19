using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Controls;
using PinPoint.Commands;
using PinPoint.Core.Geometry;
using PinPoint.Core.Interop;
using PinPoint.Core.Settings;
using PinPoint.Core.Windows;
using PinPoint.Helpers;
using PinPoint.Views;

namespace PinPoint.Services;

/// <summary>
/// Quick slots: a few glass buttons that float on top, each running any PinPoint action with one click, for when
/// you're on the mouse and don't want to reach for shortcuts. A diamond of four by default (like skill slots in a
/// game), or a row/column of up to eight, in three sizes. Right-click a slot to choose its action; drag any slot
/// to move the group. They stay put during Peek and Hide all (they're how you drive those), stay out of snips,
/// and remember where they were.
/// </summary>
public sealed class QuickSlots
{
    private const int GapDip = 5;

    private readonly DispatcherQueue _dispatcher;
    private readonly SettingsService _settings;
    private readonly CommandRegistry _commands;
    private readonly PinManager _pins;
    private readonly List<QuickSlotTile> _tiles = [];
    private readonly DispatcherQueueTimer _saveTimer;

    // Drag state: every tile's window rect at the start, so the group moves as one rigid piece.
    private List<PixelRect>? _dragStart;
    private (int X, int Y) _dragOrigin;

    public QuickSlots(DispatcherQueue dispatcher, SettingsService settings, CommandRegistry commands, PinManager pins)
    {
        _dispatcher = dispatcher;
        _settings = settings;
        _commands = commands;
        _pins = pins;

        _saveTimer = dispatcher.CreateTimer();
        _saveTimer.IsRepeating = false;
        _saveTimer.Interval = TimeSpan.FromMilliseconds(300);
        _saveTimer.Tick += (_, _) => _settings.Flush();

        pins.StateChanged += (_, _) => RefreshStates();
        pins.Raised += RaiseTiles;
        pins.CaptureHook = HideForCapture;
        pins.ExtraTopmost = () => _tiles.Where(t => t.IsShown).Select(t => t.Handle); // a game covering just the slots brings them back up too
    }

    public const string ToggleCommandId = "toggleQuickSlots";

    public bool IsVisible => _tiles.Count > 0;

    private QuickSlotSettings Config => _settings.Current.QuickSlots;

    /// <summary>Shows them if they were showing when PinPoint last closed.</summary>
    public void RestoreIfVisible()
    {
        if (Config.Visible) Show();
    }

    public void Toggle()
    {
        if (IsVisible) Hide();
        else Show();
    }

    public void Show()
    {
        if (!IsVisible) Build();
        _settings.Update(s => s.QuickSlots.Visible = true);
        _pins.NotifyStateChanged();
    }

    public void Hide()
    {
        foreach (var tile in _tiles) tile.Close();
        _tiles.Clear();
        _settings.Update(s => s.QuickSlots.Visible = false);
        _pins.NotifyStateChanged();
    }

    public void Close()
    {
        foreach (var tile in _tiles) tile.Close();
        _tiles.Clear();
    }

    // ---- Layout -----------------------------------------------------------------------------

    private void Build()
    {
        Close();
        int count = QuickSlotLayout.SlotCount(Config.Arrangement, Config.Commands.Count);
        for (int i = 0; i < count; i++)
        {
            var tile = new QuickSlotTile(i);
            tile.Clicked += OnClicked;
            tile.MenuRequested += OnMenuRequested;
            tile.DragStarted += OnDragStarted;
            tile.DragMoved += OnDragMoved;
            tile.DragEnded += OnDragEnded;
            _tiles.Add(tile);
        }

        Layout(show: true);
        RefreshStates();
    }

    /// <summary>Places every tile for the current origin, arrangement and size (DPI of the monitor it's on).</summary>
    private void Layout(bool show = false)
    {
        if (_tiles.Count == 0) return;

        var origin = Origin();
        double scale = ScaleAt(origin.X, origin.Y);
        int tile = (int)Math.Round(Config.TileSize * scale), gap = (int)Math.Round(GapDip * scale);
        var offsets = QuickSlotLayout.Offsets(Config.Arrangement, _tiles.Count, tile, gap);
        var (width, height) = QuickSlotLayout.Size(Config.Arrangement, _tiles.Count, tile, gap);
        var (_, work) = WindowEnumerator.GetMonitorAt(origin.X + width / 2, origin.Y + height / 2);
        var (x, y) = QuickSlotLayout.Clamp(origin.X, origin.Y, width, height, work);

        for (int i = 0; i < _tiles.Count; i++)
        {
            _tiles[i].Place(new PixelRect(x + offsets[i].X, y + offsets[i].Y, tile, tile));
            if (show && ShouldShow(_tiles[i])) _tiles[i].ShowTile();
        }

        if (x != Config.X || y != Config.Y) _settings.Update(s => (s.QuickSlots.X, s.QuickSlots.Y) = (x, y));
        RefreshStates();
    }

    /// <summary>Saved spot, or (first time) the right edge of the monitor under the mouse, middle height.</summary>
    private (int X, int Y) Origin()
    {
        if (Config.X is { } x && Config.Y is { } y) return (x, y);
        Win32.GetCursorPos(out var cursor);
        var (_, work) = WindowEnumerator.GetMonitorAt(cursor.X, cursor.Y);
        double scale = ScaleAt(cursor.X, cursor.Y);
        var (width, height) = QuickSlotLayout.Size(Config.Arrangement, _tiles.Count, (int)(Config.TileSize * scale), (int)(GapDip * scale));
        return (work.Right - width - (int)(24 * scale), work.Y + (work.Height - height) / 2);
    }

    private static double ScaleAt(int x, int y)
    {
        nint monitor = Win32.MonitorFromPoint(new Win32.POINT { X = x, Y = y }, Win32.MONITOR_DEFAULTTONEAREST);
        return Win32.GetDpiForMonitor(monitor, 0 /*MDT_EFFECTIVE_DPI*/, out uint dpi, out _) == 0 ? dpi / 96.0 : 1.0;
    }

    // ---- Actions -----------------------------------------------------------------------------

    private AppCommand? CommandAt(int index)
    {
        string? id = index < Config.Commands.Count ? Config.Commands[index] : null;
        return string.IsNullOrEmpty(id) ? null : _commands.All.FirstOrDefault(c => c.Id == id);
    }

    /// <summary>
    /// "Hide all" means a clear screen, slots included, except a slot that is itself Hide all: that one stays,
    /// so you can bring everything back with the mouse. During a screen capture none show.
    /// </summary>
    private bool ShouldShow(QuickSlotTile tile) => !_capturing; // the slots are the controls: they stay even when pins hide

    private bool _capturing;

    private void ApplyVisibility()
    {
        foreach (var tile in _tiles)
        {
            bool show = ShouldShow(tile);
            if (show == tile.IsShown) continue;
            if (show) tile.ShowTile();
            else tile.HideTile();
        }
    }

    private void RefreshStates()
    {
        if (_tiles.Count == 0) return;
        ApplyVisibility();
        double iconSize = Math.Round(Config.TileSize * 0.38);
        foreach (var tile in _tiles)
        {
            var command = CommandAt(tile.Index);
            if (command is null)
            {
                tile.SetAction("\uE710", null, "Empty slot · right-click to choose an action", isOn: false, isEmpty: true, iconSize);
                continue;
            }

            string? key = _settings.GetHotkey(command)?.ToDisplayString();
            string tip = key is null ? command.Title : $"{command.Title}   {key}";
            tile.SetAction(command.Glyph, command.BadgeGlyph, $"{tip}\nRight-click to change · drag to move", command.IsChecked?.Invoke() ?? false, isEmpty: false, iconSize);
        }
    }

    private void OnClicked(QuickSlotTile tile)
    {
        var command = CommandAt(tile.Index);
        if (command is null)
        {
            OnMenuRequested(tile, new Windows.Foundation.Point(Config.TileSize / 2.0, Config.TileSize / 2.0));
            return;
        }

        // After the click finishes, so the action sees a settled pointer (and Snip sees no pressed button).
        _dispatcher.TryEnqueue(() =>
        {
            command.Execute();
            RefreshStates();
        });
    }

    // ---- Menu --------------------------------------------------------------------------------

    private void OnMenuRequested(QuickSlotTile tile, Windows.Foundation.Point at)
    {
        var menu = new MenuFlyout { ShouldConstrainToRootBounds = false };

        var action = new MenuFlyoutSubItem { Text = "Action", Icon = new FontIcon { Glyph = "" } };
        string current = CommandAt(tile.Index)?.Id ?? string.Empty;
        foreach (var command in _commands.All.Where(c => c.Id != ToggleCommandId))
        {
            var item = new RadioMenuFlyoutItem
            {
                Text = command.Title,
                GroupName = "slotAction",
                IsChecked = command.Id == current,
                Icon = new FontIcon { Glyph = command.Glyph },
            };
            string id = command.Id;
            item.Click += (_, _) => SetSlot(tile.Index, id);
            action.Items.Add(item);
        }

        action.Items.Add(new MenuFlyoutSeparator());
        var empty = new RadioMenuFlyoutItem { Text = "Empty", GroupName = "slotAction", IsChecked = current.Length == 0 };
        empty.Click += (_, _) => SetSlot(tile.Index, string.Empty);
        action.Items.Add(empty);
        menu.Items.Add(action);

        var layout = new MenuFlyoutSubItem { Text = "Layout", Icon = new FontIcon { Glyph = "" } };
        foreach (var (arrangement, label) in new[] { (QuickSlotArrangement.Diamond, "Diamond (4)"), (QuickSlotArrangement.Row, "Row"), (QuickSlotArrangement.Column, "Column") })
        {
            var item = new RadioMenuFlyoutItem { Text = label, GroupName = "slotLayout", IsChecked = Config.Arrangement == arrangement };
            item.Click += (_, _) => SetArrangement(arrangement);
            layout.Items.Add(item);
        }

        menu.Items.Add(layout);

        var size = new MenuFlyoutSubItem { Text = "Size", Icon = new FontIcon { Glyph = "" } };
        foreach (var (dip, label) in new[] { (36, "Small"), (44, "Medium"), (56, "Large") })
        {
            var item = new RadioMenuFlyoutItem { Text = label, GroupName = "slotSize", IsChecked = Config.TileSize == dip };
            item.Click += (_, _) =>
            {
                _settings.Update(s => s.QuickSlots.TileSize = dip);
                Layout();
            };
            size.Items.Add(item);
        }

        menu.Items.Add(size);

        if (Config.Arrangement != QuickSlotArrangement.Diamond)
        {
            menu.Items.Add(new MenuFlyoutSeparator());
            var add = new MenuFlyoutItem { Text = "Add a slot", Icon = new FontIcon { Glyph = "" }, IsEnabled = _tiles.Count < QuickSlotLayout.MaxSlots };
            add.Click += (_, _) => SetSlotCount(_tiles.Count + 1);
            var remove = new MenuFlyoutItem { Text = "Remove this slot", Icon = new FontIcon { Glyph = "" }, IsEnabled = _tiles.Count > 1 };
            remove.Click += (_, _) => RemoveSlot(tile.Index);
            menu.Items.Add(add);
            menu.Items.Add(remove);
        }

        menu.Items.Add(new MenuFlyoutSeparator());
        var hide = new MenuFlyoutItem { Text = "Hide quick slots", Icon = new FontIcon { Glyph = "" } };
        hide.Click += (_, _) => Hide();
        menu.Items.Add(hide);

        tile.ShowMenu(menu, at);
    }

    private void SetSlot(int index, string commandId)
    {
        _settings.Update(s =>
        {
            while (s.QuickSlots.Commands.Count <= index) s.QuickSlots.Commands.Add(string.Empty);
            s.QuickSlots.Commands[index] = commandId;
        });
        RefreshStates();
    }

    private void SetArrangement(QuickSlotArrangement arrangement)
    {
        _settings.Update(s => s.QuickSlots.Arrangement = arrangement);
        Build();
    }

    private void SetSlotCount(int count)
    {
        _settings.Update(s =>
        {
            while (s.QuickSlots.Commands.Count < count) s.QuickSlots.Commands.Add(string.Empty);
            while (s.QuickSlots.Commands.Count > count) s.QuickSlots.Commands.RemoveAt(s.QuickSlots.Commands.Count - 1);
        });
        Build();
    }

    private void RemoveSlot(int index)
    {
        _settings.Update(s =>
        {
            if (index < s.QuickSlots.Commands.Count) s.QuickSlots.Commands.RemoveAt(index);
        });
        Build();
    }

    // ---- Drag the group --------------------------------------------------------------------------

    private void OnDragStarted(QuickSlotTile tile)
    {
        _dragStart = [.. _tiles.Select(t => t.WindowRect)];
        _dragOrigin = (Config.X ?? 0, Config.Y ?? 0);
    }

    private void OnDragMoved(QuickSlotTile tile, int dx, int dy)
    {
        if (_dragStart is null) return;
        Win32.MoveWindowsTogether([.. _tiles.Select((t, i) => (t.Handle, _dragStart[i].X + dx, _dragStart[i].Y + dy))]);
    }

    private void OnDragEnded(QuickSlotTile tile)
    {
        if (_dragStart is null) return;
        var moved = _tiles[0].WindowRect;
        int dx = moved.X - _dragStart[0].X, dy = moved.Y - _dragStart[0].Y;
        _dragStart = null;
        _settings.Update(s => (s.QuickSlots.X, s.QuickSlots.Y) = (_dragOrigin.X + dx, _dragOrigin.Y + dy));
        Layout(); // exact placement: back on screen if dropped off it, right size on a different-DPI monitor
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    // ---- Staying on top, out of snips --------------------------------------------------------------

    private void RaiseTiles()
    {
        foreach (var tile in _tiles) tile.RaiseTopmost();
    }

    /// <summary>Snips shouldn't catch the slots: hidden for the instant the screen is captured.</summary>
    private bool HideForCapture(bool capturing)
    {
        if (_tiles.Count == 0) return false;
        _capturing = capturing;
        ApplyVisibility();
        return true;
    }
}
