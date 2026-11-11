using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using PinPoint.Core.Geometry;
using PinPoint.Core.Interop;
using PinPoint.Core.Timing;
using PinPoint.Core.Windows;
using PinPoint.Helpers;
using Windows.Graphics;

namespace PinPoint.Views;

/// <summary>
/// A small pinned stopwatch / countdown. Like snips it never takes focus, so you can start, pause or reset it
/// without leaving your game. Right-click for the mode, the countdown length and opacity; scroll over the time
/// to change the countdown length (Shift: 10 seconds at a time). When a countdown ends it beeps and flashes.
/// </summary>
public sealed partial class TimerPinWindow : Window, IPinSurface
{
    private const int WidthDip = 210, HeightDip = 88;

    private static readonly (string Label, int Seconds)[] s_presets =
    [
        ("30 seconds", 30), ("1 minute", 60), ("2 minutes", 120), ("3 minutes", 180), ("5 minutes", 300),
        ("10 minutes", 600), ("15 minutes", 900), ("20 minutes", 1200), ("30 minutes", 1800), ("45 minutes", 2700),
        ("1 hour", 3600),
    ];

    private readonly nint _hwnd;
    private readonly PinTimer _timer = new();
    private readonly WindowDragger _drag;
    private readonly DispatcherQueueTimer _tick;
    private readonly DispatcherQueueTimer _flash;
    private readonly Brush _normalBackground;
    private readonly SolidColorBrush _alarmBackground = new(ColorHelper.FromArgb(255, 196, 43, 28));
    private double _opacity = 1.0;
    private bool _clickThrough;
    private int _flashesLeft;

    public TimerPinWindow()
    {
        InitializeComponent();
        _hwnd = this.GetHandle();
        _drag = new WindowDragger(AppWindow);
        _normalBackground = Root.Background;

        WindowHelpers.MakeToolSurface(AppWindow, alwaysOnTop: true);
        WindowStyler.SetExStyleFlag(_hwnd, Win32.WS_EX_TOOLWINDOW, true);
        WindowStyler.SetExStyleFlag(_hwnd, Win32.WS_EX_NOACTIVATE, true);

        foreach (var (label, seconds) in s_presets)
        {
            var item = new MenuFlyoutItem { Text = label };
            item.Click += (_, _) =>
            {
                StopAlarm();
                _timer.SetDuration(TimeSpan.FromSeconds(seconds));
                UpdateAll();
            };
            DurationMenu.Items.Add(item);
        }

        _tick = DispatcherQueue.CreateTimer();
        _tick.Interval = TimeSpan.FromMilliseconds(200);
        _tick.Tick += (_, _) => OnTick();

        _flash = DispatcherQueue.CreateTimer();
        _flash.Interval = TimeSpan.FromMilliseconds(400);
        _flash.Tick += (_, _) => OnFlash();

        ShowSoundState();
        AppHost.Current.Settings.Changed += OnSettingsChanged; // all timers share the silent setting
        Closed += (_, _) => AppHost.Current.Settings.Changed -= OnSettingsChanged;

        // Switching back to the game while typing keeps the typed time.
        Activated += (_, e) =>
        {
            if (e.WindowActivationState == WindowActivationState.Deactivated) EndEdit(commit: true);
        };

        UpdateAll();
    }

    /// <summary>Raised when the mode, length or running state changes (for the Pins list).</summary>
    public event EventHandler? Changed;

    /// <summary>A countdown just reached zero.</summary>
    public event EventHandler? Finished;

    /// <summary>"Stopwatch" or "Timer · 5:00", for the Pins list.</summary>
    public string Title => _timer.Mode == PinTimerMode.Stopwatch ? "Stopwatch" : $"Timer · {PinTimer.Format(_timer.Duration, roundUp: true)}";

    public bool IsRunning => _timer.IsRunning;

    public nint Handle => _hwnd;

    public PixelRect ScreenBounds => WindowHelpers.GetClientScreenRect(_hwnd, AppWindow);

    /// <summary>Never takes focus, so there's nothing to interrupt (the menu is a popup of its own).</summary>
    public bool IsInteracting => _drag.IsDragging || Menu.IsOpen || _editing;

    public double PinOpacity
    {
        get => _opacity;
        set
        {
            _opacity = Math.Clamp(Math.Round(value, 2), 0.1, 1.0);
            ApplyLayering();
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
        }
    }

    /// <summary>Opens near the mouse, kept inside that monitor's work area.</summary>
    public void ShowNearCursor()
    {
        Win32.GetCursorPos(out var cursor);
        var (_, work) = WindowEnumerator.GetMonitorAt(cursor.X, cursor.Y);
        AppWindow.Move(new PointInt32(work.X + 20, work.Y + 20)); // pick up that monitor's DPI
        double scale = WindowHelpers.GetScale(_hwnd);
        int width = (int)(WidthDip * scale), height = (int)(HeightDip * scale);
        int x = Math.Clamp(cursor.X - width / 2, work.X, Math.Max(work.X, work.Right - width));
        int y = Math.Clamp(cursor.Y - 20, work.Y, Math.Max(work.Y, work.Bottom - height));
        WindowHelpers.SetClientScreenRect(_hwnd, AppWindow, new PixelRect(x, y, width, height));
        AppWindow.Show(activateWindow: false);
        WindowStyler.RaiseTopmost(_hwnd);
    }

    public void NudgeOpacity(int direction) => PinOpacity += direction * 0.1;

    public void SetHidden(bool hidden)
    {
        if (hidden) AppWindow.Hide();
        else AppWindow.Show(activateWindow: false);
    }

    public void RaiseTopmost() => WindowStyler.RaiseTopmost(_hwnd);

    public bool SetTemporarilyOpaque(bool opaque)
    {
        if (_opacity >= 0.999) return false;
        if (opaque) WindowStyler.SetAlpha(_hwnd, 255);
        else ApplyLayering();
        return true;
    }

    public void CloseTimer()
    {
        _tick.Stop();
        _flash.Stop();
        Close();
    }

    // ---- Clock ---------------------------------------------------------------------------------

    private void OnTick()
    {
        if (_timer.CheckFinished())
        {
            UpdateAll();
            StartAlarm();
            Finished?.Invoke(this, EventArgs.Empty);
            return;
        }

        // Only touch the text when the shown second changes: no needless redraws over a game.
        string text = _timer.DisplayText;
        if (TimeText.Text != text) TimeText.Text = text;
    }

    private void UpdateAll()
    {
        TimeText.Text = _timer.DisplayText;
        ModeText.Text = _timer.Mode == PinTimerMode.Stopwatch ? "Stopwatch" : "Timer";
        StartIcon.Glyph = _timer.IsRunning ? "" : ""; // pause : play
        if (_timer.IsRunning) _tick.Start();
        else _tick.Stop();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void Start_Click(object sender, RoutedEventArgs e)
    {
        StopAlarm();
        _timer.Toggle();
        UpdateAll();
    }

    private void Reset_Click(object sender, RoutedEventArgs e)
    {
        StopAlarm();
        _timer.Reset();
        UpdateAll();
    }

    // ---- Typing a time (425 = 4:25) -------------------------------------------------------------
    // The window normally never takes focus; while typing it has to, so it's activatable just for that.

    private bool _editing;

    private void Root_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        BeginEdit();
        e.Handled = true;
    }

    private void TypeTime_Click(object sender, RoutedEventArgs e) => BeginEdit();

    private void BeginEdit()
    {
        if (_editing) return;
        _editing = true;
        StopAlarm();
        _drag.Cancel();

        WindowStyler.SetExStyleFlag(_hwnd, Win32.WS_EX_NOACTIVATE, false);
        Win32.SetForegroundWindow(_hwnd);
        Activate();

        TimeText.Visibility = Visibility.Collapsed;
        TimeEdit.Text = string.Empty;
        TimeEdit.Visibility = Visibility.Visible;
        DispatcherQueue.TryEnqueue(() => TimeEdit.Focus(FocusState.Programmatic)); // once the window is active
    }

    private void EndEdit(bool commit)
    {
        if (!_editing) return;
        _editing = false;

        if (commit && PinTimer.TryParseTyped(TimeEdit.Text, out var time)) _timer.SetDuration(time);

        TimeEdit.Visibility = Visibility.Collapsed;
        TimeText.Visibility = Visibility.Visible;
        WindowStyler.SetExStyleFlag(_hwnd, Win32.WS_EX_NOACTIVATE, true);
        UpdateAll();
    }

    private void TimeEdit_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Enter)
        {
            EndEdit(commit: true);
            e.Handled = true;
        }
        else if (e.Key == Windows.System.VirtualKey.Escape)
        {
            EndEdit(commit: false);
            e.Handled = true;
        }
    }

    private void TimeText_PointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        if (_timer.Mode != PinTimerMode.Countdown) return;
        var point = e.GetCurrentPoint(TimeText);
        bool shift = e.KeyModifiers.HasFlag(Windows.System.VirtualKeyModifiers.Shift);
        int sign = point.Properties.MouseWheelDelta > 0 ? 1 : -1;
        _timer.AdjustDuration(TimeSpan.FromSeconds(sign * (shift ? 10 : 60)));
        UpdateAll();
        e.Handled = true;
    }

    // ---- Alarm -----------------------------------------------------------------------------------

    private void StartAlarm()
    {
        _flashesLeft = 14; // about 5.5 s
        if (!IsSilent) Win32.MessageBeep(Win32.MB_ICONEXCLAMATION);
        _flash.Start();
    }

    private void OnFlash()
    {
        _flashesLeft--;
        Root.Background = _flashesLeft % 2 == 1 ? _alarmBackground : _normalBackground;
        if (!IsSilent && _flashesLeft % 4 == 0 && _flashesLeft >= 6) Win32.MessageBeep(Win32.MB_ICONEXCLAMATION);
        if (_flashesLeft <= 0) StopAlarm();
    }

    private void StopAlarm()
    {
        _flash.Stop();
        _flashesLeft = 0;
        Root.Background = _normalBackground;
    }

    // ---- Menu --------------------------------------------------------------------------------------

    /// <summary>Silent timers just flash when they end: no beeps, no pop-up. Remembered for new timers too.</summary>
    public static bool IsSilent => AppHost.Current.Settings.Current.TimerSilent;

    private void Silent_Click(object sender, RoutedEventArgs e) =>
        AppHost.Current.Settings.Update(s => s.TimerSilent = SilentItem.IsChecked);

    /// <summary>The header's speaker button: sound on/off for every timer.</summary>
    private void SoundButton_Click(object sender, RoutedEventArgs e) =>
        AppHost.Current.Settings.Update(s => s.TimerSilent = !s.TimerSilent);

    private void ShowSoundState()
    {
        SoundIcon.Glyph = IsSilent ? "" : ""; // mute : volume
        ToolTipService.SetToolTip(SoundButton, IsSilent
            ? "Silent: when time's up it only flashes. Click to turn the sound on."
            : "Sound on: when time's up it beeps. Click for silent (flash only).");
    }

    private void OnSettingsChanged(object? sender, EventArgs e) => DispatcherQueue.TryEnqueue(ShowSoundState);

    private void Menu_Opening(object sender, object e)
    {
        SilentItem.IsChecked = IsSilent;
        StopwatchItem.IsChecked = _timer.Mode == PinTimerMode.Stopwatch;
        CountdownItem.IsChecked = _timer.Mode == PinTimerMode.Countdown;
        foreach (var item in new[] { Opacity100, Opacity80, Opacity60, Opacity40, Opacity20 })
            item.IsChecked = Math.Abs(double.Parse((string)item.Tag, System.Globalization.CultureInfo.InvariantCulture) - _opacity) < 0.01;
    }

    private void Stopwatch_Click(object sender, RoutedEventArgs e)
    {
        StopAlarm();
        _timer.SetMode(PinTimerMode.Stopwatch);
        UpdateAll();
    }

    private void Countdown_Click(object sender, RoutedEventArgs e)
    {
        StopAlarm();
        _timer.SetMode(PinTimerMode.Countdown);
        UpdateAll();
    }

    private void Opacity_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string tag })
            PinOpacity = double.Parse(tag, System.Globalization.CultureInfo.InvariantCulture);
    }

    private void Close_Click(object sender, RoutedEventArgs e) => CloseTimer();

    // ---- Drag anywhere, right-click menu ----------------------------------------------------------

    private void Root_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        StopAlarm(); // any click quiets a finished countdown
        if (_editing) return; // clicks while typing go to the text box
        if (!e.GetCurrentPoint(Root).Properties.IsLeftButtonPressed) return;
        _drag.Begin(Root, e.Pointer);
        e.Handled = true;
    }

    private void Root_PointerMoved(object sender, PointerRoutedEventArgs e) => _drag.Update();

    private void Root_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(Root);
        if (point.Properties.PointerUpdateKind == PointerUpdateKind.RightButtonReleased)
        {
            Menu.ShowAt(Root, new FlyoutShowOptions { Position = point.Position });
            e.Handled = true;
            return;
        }

        _drag.End(Root, e.Pointer);
    }

    private void Root_PointerCaptureLost(object sender, PointerRoutedEventArgs e) => _drag.Cancel();

    private void ApplyLayering()
    {
        if (_opacity < 0.999 || _clickThrough)
            WindowStyler.SetAlpha(_hwnd, (byte)Math.Round(_opacity * 255));
        else
            WindowStyler.ClearLayered(_hwnd);
    }
}
