using System.Diagnostics;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Imaging;
using PinPoint.Core.Capture;
using PinPoint.Core.Diagnostics;
using PinPoint.Core.Geometry;
using PinPoint.Core.Interop;
using PinPoint.Core.Windows;
using PinPoint.Helpers;
using PinPoint.Services;
using Windows.Graphics;
using Windows.Storage.Pickers;

namespace PinPoint.Views;

/// <summary>
/// A floating, always-on-top snip. Drag to move, wheel to zoom, fade-key+wheel or
/// right-button+wheel for opacity, middle-click/Esc to close, right-click for more.
/// </summary>
public sealed partial class ImagePinWindow : Window
{
    private const double ZoomStep = 1.1;
    private const int MinZoomStep = -24; // ≈ 10 %
    private const int MaxZoomStep = 22;  // ≈ 800 %
    private const double OpacityStep = 0.1;

    private readonly nint _hwnd;
    private readonly bool _takesFocus;
    private readonly SnipStorage _storage;
    private readonly SettingsService _settings;
    private readonly DispatcherQueueTimer _badgeTimer;

    private int _zoomStep;
    private double _opacity = 1.0;
    private bool _clickThrough;
    private bool _dragging;
    private bool _rightHeld;
    private bool _scrolledWhileRightHeld;
    private Win32.POINT _dragCursorStart;
    private PointInt32 _dragWindowStart;
    private string? _savedPath;

    public ImagePinWindow(CapturedFrame frame, SnipStorage storage, SettingsService settings)
    {
        InitializeComponent();
        Frame = frame;
        _storage = storage;
        _settings = settings;
        _takesFocus = settings.Current.PinsTakeFocus;
        _hwnd = this.GetHandle();

        WindowHelpers.MakeToolSurface(AppWindow, alwaysOnTop: true);
        WindowStyler.SetExStyleFlag(_hwnd, Win32.WS_EX_TOOLWINDOW, true);
        if (!_takesFocus) WindowStyler.SetExStyleFlag(_hwnd, Win32.WS_EX_NOACTIVATE, true);

        Bitmap = frame.ToWriteableBitmap();
        PinImage.Source = Bitmap;

        _badgeTimer = DispatcherQueue.CreateTimer();
        _badgeTimer.IsRepeating = false;
        _badgeTimer.Interval = TimeSpan.FromMilliseconds(900);
        _badgeTimer.Tick += (_, _) => Badge.Opacity = 0;

        Closed += (_, _) =>
        {
            _badgeTimer.Stop();
            PinImage.Source = null;
        };
    }

    /// <summary>Raised when something shown in the Pins list changes (opacity, file name).</summary>
    public event EventHandler? Changed;

    public CapturedFrame Frame { get; }

    public WriteableBitmap Bitmap { get; }

    public string? SavedPath
    {
        get => _savedPath;
        set
        {
            _savedPath = value;
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    public nint Handle => _hwnd;

    public PixelRect ScreenBounds => WindowHelpers.GetClientScreenRect(_hwnd, AppWindow);

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
            SetHoverChrome(false);
        }
    }

    /// <summary>Shows the pin with its image exactly over <paramref name="client"/> (where it was snipped).</summary>
    public void ShowAt(PixelRect client)
    {
        // Move onto the target monitor first so any DPI change happens before sizing.
        AppWindow.Move(new PointInt32(client.X, client.Y));
        WindowHelpers.SetClientScreenRect(_hwnd, AppWindow, client);
        AppWindow.Show(activateWindow: _takesFocus);
        WindowHelpers.SetClientScreenRect(_hwnd, AppWindow, client);
        WindowStyler.RaiseTopmost(_hwnd);
    }

    public void SetHidden(bool hidden)
    {
        if (hidden) AppWindow.Hide();
        else AppWindow.Show(activateWindow: false);
    }

    public void RaiseTopmost() => WindowStyler.RaiseTopmost(_hwnd);

    /// <summary>Shows the snip fully opaque for a screen capture without changing its opacity setting.</summary>
    public bool SetTemporarilyOpaque(bool opaque)
    {
        if (_opacity >= 0.999) return false;
        if (opaque) WindowStyler.SetAlpha(_hwnd, 255);
        else ApplyLayering();
        return true;
    }

    /// <summary>Steps opacity up or down by 10 % and shows the new value on the snip.</summary>
    public void NudgeOpacity(int direction)
    {
        PinOpacity += direction * OpacityStep;
        ShowBadge($"Opacity {PinOpacity:P0}");
    }

    // ---- Pointer: drag, zoom, hover ----------------------------------------------------------

    private void Root_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        var properties = e.GetCurrentPoint(Root).Properties;
        if (properties.IsMiddleButtonPressed)
        {
            Close();
            e.Handled = true;
            return;
        }

        if (properties.IsRightButtonPressed)
        {
            // Menu decision waits for the release: a scroll while held means "fade", not "menu".
            _rightHeld = true;
            _scrolledWhileRightHeld = false;
            e.Handled = true;
            return;
        }

        if (!properties.IsLeftButtonPressed) return;

        // Track in screen space: pointer coords relative to a moving window would feed back on themselves.
        Win32.GetCursorPos(out _dragCursorStart);
        _dragWindowStart = AppWindow.Position;
        _dragging = Root.CapturePointer(e.Pointer);
        e.Handled = true;
    }

    private void Root_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_dragging) return;
        Win32.GetCursorPos(out var cursor);
        AppWindow.Move(new PointInt32(
            _dragWindowStart.X + cursor.X - _dragCursorStart.X,
            _dragWindowStart.Y + cursor.Y - _dragCursorStart.Y));
    }

    private void Root_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(Root);
        if (point.Properties.PointerUpdateKind == PointerUpdateKind.RightButtonReleased)
        {
            bool showMenu = _rightHeld && !_scrolledWhileRightHeld;
            _rightHeld = false;
            if (showMenu) Menu.ShowAt(Root, new FlyoutShowOptions { Position = point.Position });
            e.Handled = true;
            return;
        }

        if (!_dragging) return;
        _dragging = false;
        Root.ReleasePointerCapture(e.Pointer);
    }

    private void Root_PointerCaptureLost(object sender, PointerRoutedEventArgs e) => _dragging = false;

    private void Root_PointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        var properties = e.GetCurrentPoint(Root).Properties;
        int delta = properties.MouseWheelDelta;
        if (delta == 0) return;

        // The right button comes with the wheel message itself, so it works even when Windows hides
        // keyboard state from us (e.g. an elevated game has focus). The fade key is the convenient path;
        // GetAsyncKeyState because our thread's key state is stale while unfocused.
        bool rightButton = properties.IsRightButtonPressed;
        if (rightButton || Win32.IsKeyDown(_settings.FadeModifierKey))
        {
            if (rightButton) _scrolledWhileRightHeld = true;
            NudgeOpacity(delta > 0 ? 1 : -1);
        }
        else
        {
            Win32.GetCursorPos(out var cursor);
            SetZoomStep(_zoomStep + (delta > 0 ? 1 : -1), cursor);
        }

        e.Handled = true;
    }

    private void Root_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e) => ResetZoom();

    private void Root_PointerEntered(object sender, PointerRoutedEventArgs e) => SetHoverChrome(!_clickThrough);

    private void Root_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (!_dragging) SetHoverChrome(false);
    }

    private void SetHoverChrome(bool visible)
    {
        HoverOutline.Opacity = visible ? 1 : 0;
        CloseButton.Opacity = visible ? 1 : 0;
        CloseButton.IsHitTestVisible = visible;
    }

    // ---- Zoom / opacity ----------------------------------------------------------------------

    private void SetZoomStep(int step, Win32.POINT anchor)
    {
        step = Math.Clamp(step, MinZoomStep, MaxZoomStep);
        if (step == _zoomStep) return;

        double zoom = Math.Pow(ZoomStep, step);
        var client = ScreenBounds;

        // Keep the image point under the cursor fixed while scaling.
        double rx = client.Width > 0 ? (anchor.X - client.X) / (double)client.Width : 0.5;
        double ry = client.Height > 0 ? (anchor.Y - client.Y) / (double)client.Height : 0.5;
        int width = Math.Max(24, (int)Math.Round(Frame.Width * zoom));
        int height = Math.Max(24, (int)Math.Round(Frame.Height * zoom));
        int x = (int)Math.Round(anchor.X - rx * width);
        int y = (int)Math.Round(anchor.Y - ry * height);

        WindowHelpers.SetClientScreenRect(_hwnd, AppWindow, new PixelRect(x, y, width, height));
        _zoomStep = step;
        ShowBadge($"{zoom:P0}");
    }

    private void ResetZoom()
    {
        var client = ScreenBounds;
        SetZoomStep(0, new Win32.POINT { X = client.X + client.Width / 2, Y = client.Y + client.Height / 2 });
    }

    private void ApplyLayering()
    {
        if (_opacity < 0.999 || _clickThrough)
            WindowStyler.SetAlpha(_hwnd, (byte)Math.Round(_opacity * 255));
        else
            WindowStyler.ClearLayered(_hwnd);
    }

    private void ShowBadge(string text)
    {
        BadgeText.Text = text;
        Badge.Opacity = 1;
        _badgeTimer.Stop();
        _badgeTimer.Start();
    }

    // ---- Menu --------------------------------------------------------------------------------

    private void Menu_Opening(object sender, object e)
    {
        ShowInFolderItem.Visibility = SavedPath is null ? Visibility.Collapsed : Visibility.Visible;
        ClickThroughItem.IsChecked = _clickThrough;
        Opacity100.IsChecked = _opacity > 0.9;
        Opacity80.IsChecked = _opacity is > 0.7 and <= 0.9;
        Opacity60.IsChecked = _opacity is > 0.5 and <= 0.7;
        Opacity40.IsChecked = _opacity <= 0.5;
    }

    private async void Copy_Click(object sender, RoutedEventArgs e) => await CopyAsync();

    private async void Copy_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        await CopyAsync();
    }

    private async Task CopyAsync()
    {
        await _storage.CopyToClipboardAsync(Frame);
        ShowBadge("Copied");
    }

    private async void SaveAs_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new FileSavePicker { SuggestedFileName = SnipStorage.CreateFileName() };
            picker.FileTypeChoices.Add("PNG image", [".png"]);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, _hwnd);

            var file = await picker.PickSaveFileAsync();
            if (file is null) return;

            await _storage.SaveToAsync(Frame, file.Path);
            ShowBadge("Saved");
        }
        catch (Exception ex)
        {
            Log.Error("Save as failed", ex);
            ShowBadge("Couldn't save");
        }
    }

    private void ShowInFolder_Click(object sender, RoutedEventArgs e)
    {
        if (SavedPath is null || !File.Exists(SavedPath)) return;
        Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{SavedPath}\"") { UseShellExecute = true });
    }

    private void Opacity_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string tag } && double.TryParse(tag, System.Globalization.CultureInfo.InvariantCulture, out double value))
            PinOpacity = value;
    }

    private void ClickThrough_Click(object sender, RoutedEventArgs e) => IsClickThrough = ClickThroughItem.IsChecked;

    private void ResetZoom_Click(object sender, RoutedEventArgs e) => ResetZoom();

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void Close_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        Close();
    }
}
