using System.Collections.ObjectModel;
using Microsoft.UI.Dispatching;
using PinPoint.Core.Capture;
using PinPoint.Core.Interop;
using PinPoint.Core.Windows;
using PinPoint.ViewModels;
using PinPoint.Views;

namespace PinPoint.Services;

/// <summary>
/// Single source of truth for everything pinned: other apps' windows and PinPoint's own snip windows.
/// Also keeps pins above games that make themselves topmost.
/// </summary>
public sealed class PinManager : IDisposable
{
    private readonly WindowPinService _windows = new();
    private readonly ForegroundWatcher _foreground = new();
    private readonly List<ImagePinWindow> _snips = [];
    private readonly SettingsService _settings;
    private readonly SnipStorage _storage;
    private readonly Notifier _notifier;
    private readonly DispatcherQueueTimer _maintenance;
    private readonly DispatcherQueueTimer _raiseAgain;

    public PinManager(DispatcherQueue dispatcher, SettingsService settings, SnipStorage storage, Notifier notifier)
    {
        _settings = settings;
        _storage = storage;
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

    public bool IsClickThrough =>
        _snips.Exists(s => s.IsClickThrough) || _windows.Pins.Any(p => p.IsClickThrough);

    public bool HasPins => Items.Count > 0;

    public bool HasSnips => _snips.Count > 0;

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
        var window = new ImagePinWindow(frame, _storage, _settings);
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
        foreach (var snip in _snips) snip.SetHidden(IsHidden);
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

        bool enable = !IsClickThrough;
        foreach (var snip in _snips) snip.IsClickThrough = enable;
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
        var snip = _snips.FindLast(s => s.ScreenBounds.Contains(cursor.X, cursor.Y));
        var window = snip is null
            ? _windows.Pins.LastOrDefault(p => WindowEnumerator.GetBounds(p.Handle).Contains(cursor.X, cursor.Y))
            : null;

        if (snip is not null)
        {
            snip.NudgeOpacity(direction);
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
            foreach (var s in _snips) s.PinOpacity += direction * 0.1;
            foreach (var p in _windows.Pins) _windows.SetOpacity(p, p.Opacity + direction * 0.1);
            double shown = _snips.Count > 0 ? _snips[^1].PinOpacity : _windows.Pins[^1].Opacity;
            _notifier.Show("", $"All pins · opacity {shown:P0}");
        }

        foreach (var item in Items) item.Refresh();
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
    }

    private void ShowAll()
    {
        if (!IsHidden) return;
        IsHidden = false;
        _windows.SetHidden(false);
        foreach (var snip in _snips) snip.SetHidden(false);
    }

    private void RaiseAll()
    {
        if (IsHidden) return;
        _windows.RaiseAll();
        foreach (var snip in _snips) snip.RaiseTopmost();
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
