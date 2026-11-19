using System.Numerics;
using Microsoft.UI.Composition;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using PinPoint.Core.Geometry;
using PinPoint.Core.Interop;
using PinPoint.Core.Windows;
using PinPoint.Helpers;
using Windows.Graphics;
using WinRT;

namespace PinPoint.Views;

/// <summary>
/// One quick slot: a small square of real desktop glass (the system acrylic blurs whatever is behind it, with
/// Windows 11's rounded corners and soft shadow) holding one action's icon. Each slot is its own tiny window,
/// which is what lets the glass be true blur and the shadow hug each tile. Never takes focus: clicking it runs
/// the action without leaving your game or page. Dragging moves the whole group (QuickSlots does the moving).
/// </summary>
public sealed class QuickSlotTile : Window
{
    private const double DragThreshold = 4; // px before a press becomes a drag (a click otherwise)

    private readonly nint _hwnd;
    private readonly Grid _root;
    private readonly Border _hover;
    private readonly Border _active;
    private readonly FontIcon _icon;
    private DesktopAcrylicController? _acrylic;
    private SystemBackdropConfiguration? _backdropConfig;

    private bool _pressed, _dragging;
    private Win32.POINT _pressAt;

    public QuickSlotTile(int index)
    {
        Index = index;
        _hwnd = this.GetHandle();
        WindowHelpers.MakeToolSurface(AppWindow, alwaysOnTop: true); // rounded corners, hairline border, shadow
        WindowStyler.SetExStyleFlag(_hwnd, Win32.WS_EX_NOACTIVATE | Win32.WS_EX_TOOLWINDOW, true);

        var res = Application.Current.Resources;
        _hover = new Border
        {
            Background = new SolidColorBrush(Microsoft.UI.Colors.White),
            Opacity = 0,
            OpacityTransition = new ScalarTransition { Duration = TimeSpan.FromMilliseconds(90) },
        };
        _active = new Border
        {
            Background = (Brush)res["AccentFillColorDefaultBrush"],
            Opacity = 0,
            OpacityTransition = new ScalarTransition { Duration = TimeSpan.FromMilliseconds(120) },
        };
        _icon = new FontIcon
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = (Brush)res["TextFillColorPrimaryBrush"],
            ScaleTransition = new Vector3Transition { Duration = TimeSpan.FromMilliseconds(80) },
        };

        _root = new Grid { Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent), Children = { _active, _hover, _icon } };
        _root.PointerEntered += (_, _) => _hover.Opacity = 0.07;
        _root.PointerExited += (_, _) =>
        {
            _hover.Opacity = 0;
            if (!_pressed) _icon.Scale = Vector3.One;
        };
        _root.PointerPressed += Root_PointerPressed;
        _root.PointerMoved += Root_PointerMoved;
        _root.PointerReleased += Root_PointerReleased;
        _root.PointerCaptureLost += (_, _) => EndPress(cancelled: true);
        // Scale about the icon's own middle, so the press dips in place.
        _icon.SizeChanged += (_, _) => _icon.CenterPoint = new Vector3((float)_icon.ActualWidth / 2, (float)_icon.ActualHeight / 2, 0);
        Content = _root;

        SetupGlass();
        Closed += (_, _) => _acrylic?.Dispose();
    }

    public int Index { get; }

    public nint Handle => _hwnd;

    /// <summary>Left-click (not a drag): run this slot's action.</summary>
    public event Action<QuickSlotTile>? Clicked;

    /// <summary>Right-click: the slot's menu, at this point (DIPs, relative to the tile).</summary>
    public event Action<QuickSlotTile, Windows.Foundation.Point>? MenuRequested;

    /// <summary>Drag started / moved by (dx, dy) screen pixels from where it started / ended.</summary>
    public event Action<QuickSlotTile>? DragStarted;

    public event Action<QuickSlotTile, int, int>? DragMoved;

    public event Action<QuickSlotTile>? DragEnded;

    /// <summary>Shows an action: its icon, tooltip, and whether it's switched on (lit with the accent colour).</summary>
    public void SetAction(string glyph, string? tooltip, bool isOn, bool isEmpty, double iconSize)
    {
        _icon.Glyph = glyph;
        _icon.FontSize = iconSize;
        _icon.Opacity = isEmpty ? 0.45 : 1;
        ToolTipService.SetToolTip(_root, tooltip);
        SetOn(isOn);
    }

    public void SetOn(bool isOn)
    {
        _active.Opacity = isOn ? 0.6 : 0; // tinted glass, not a solid block: on, but still unobtrusive
        _icon.Foreground = (Brush)Application.Current.Resources[isOn ? "TextOnAccentFillColorPrimaryBrush" : "TextFillColorPrimaryBrush"];
    }

    /// <summary>Puts the tile's square (client area) exactly on <paramref name="rect"/>.</summary>
    public void Place(PixelRect rect)
    {
        AppWindow.Move(new PointInt32(rect.X, rect.Y)); // onto that monitor first (DPI)
        WindowHelpers.SetClientScreenRect(_hwnd, AppWindow, rect);
    }

    public void ShowTile()
    {
        AppWindow.Show(activateWindow: false);
        WindowStyler.RaiseTopmost(_hwnd);
    }

    public void HideTile() => AppWindow.Hide();

    public void RaiseTopmost() => WindowStyler.RaiseTopmost(_hwnd);

    /// <summary>The whole window (frame included) in screen pixels, for moving the group as one.</summary>
    public PixelRect WindowRect
    {
        get
        {
            Win32.GetWindowRect(_hwnd, out var r);
            return PixelRect.FromRect(r);
        }
    }

    public void ShowMenu(MenuFlyout menu, Windows.Foundation.Point at) =>
        menu.ShowAt(_root, new FlyoutShowOptions { Position = at });

    // ---- Click vs drag -----------------------------------------------------------------------

    private void Root_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(_root);
        if (point.Properties.IsRightButtonPressed) return; // menu on release
        if (!point.Properties.IsLeftButtonPressed) return;

        _pressed = true;
        _dragging = false;
        Win32.GetCursorPos(out _pressAt);
        _root.CapturePointer(e.Pointer);
        _icon.Scale = new Vector3(0.88f, 0.88f, 1); // a quick "press" dip
        e.Handled = true;
    }

    private void Root_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_pressed) return;
        Win32.GetCursorPos(out var now);
        int dx = now.X - _pressAt.X, dy = now.Y - _pressAt.Y;
        if (!_dragging)
        {
            if (Math.Sqrt(dx * dx + dy * dy) < DragThreshold) return;
            _dragging = true;
            _icon.Scale = Vector3.One;
            DragStarted?.Invoke(this);
        }

        DragMoved?.Invoke(this, dx, dy);
    }

    private void Root_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(_root);
        if (point.Properties.PointerUpdateKind == PointerUpdateKind.RightButtonReleased)
        {
            MenuRequested?.Invoke(this, point.Position);
            e.Handled = true;
            return;
        }

        bool wasClick = _pressed && !_dragging;
        _root.ReleasePointerCapture(e.Pointer);
        EndPress(cancelled: false);
        if (wasClick) Clicked?.Invoke(this);
        e.Handled = true;
    }

    private void EndPress(bool cancelled)
    {
        if (!_pressed) return;
        _pressed = false;
        _icon.Scale = Vector3.One;
        if (_dragging)
        {
            _dragging = false;
            DragEnded?.Invoke(this);
        }
    }

    // ---- Glass ----------------------------------------------------------------------------------

    private void SetupGlass()
    {
        if (!DesktopAcrylicController.IsSupported()) return;

        // Driven by hand so it stays "active" glass even though the tile never gets focus.
        _backdropConfig = new SystemBackdropConfiguration { IsInputActive = true };
        UpdateTheme();
        _root.ActualThemeChanged += (_, _) => UpdateTheme();

        _acrylic = new DesktopAcrylicController { Kind = DesktopAcrylicKind.Thin }; // the lighter, clearer glass
        _acrylic.AddSystemBackdropTarget(this.As<ICompositionSupportsSystemBackdrop>());
        _acrylic.SetSystemBackdropConfiguration(_backdropConfig);
    }

    private void UpdateTheme()
    {
        if (_backdropConfig is null) return;
        _backdropConfig.Theme = _root.ActualTheme == ElementTheme.Dark ? SystemBackdropTheme.Dark : SystemBackdropTheme.Light;
    }
}
