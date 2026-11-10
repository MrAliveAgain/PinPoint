using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using PinPoint.Core.Geometry;
using PinPoint.Core.Interop;
using PinPoint.Core.Windows;
using PinPoint.Helpers;
using Windows.Graphics;

namespace PinPoint.Views;

/// <summary>
/// The crossed-out cursor that sits on a click-through note's header. The note itself lets every click
/// through (that's the point), so this tiny separate window is the one spot you can still click: left-click
/// turns click-through off for every pin, right-click also offers just that note.
/// It's made to look like part of the header, not a popup: no border, shadow, rounded corners or hover
/// effect, the header's own grey, and faded along with its note. Never takes focus, so clicking it doesn't
/// pull you out of a game.
/// </summary>
public sealed class ClickThroughBadge : Window
{
    /// <summary>DWMWCP_DONOTROUND: square corners and no system shadow.</summary>
    private const int SquareCorners = 1;

    /// <summary>DWMWA_BORDER_COLOR and DWMWA_COLOR_NONE: no Windows 11 border line.</summary>
    private const uint DwmBorderColor = 34, DwmColorNone = 0xFFFFFFFE;

    private readonly nint _hwnd;

    /// <summary>Kept invisible (alpha 0) until the content has drawn once, so it never flashes white.</summary>
    private bool _rendered;
    private byte _alpha = 255;

    public ClickThroughBadge()
    {
        _hwnd = this.GetHandle();
        WindowHelpers.MakeToolSurface(AppWindow, alwaysOnTop: true);
        WindowStyler.SetExStyleFlag(_hwnd, Win32.WS_EX_TOOLWINDOW, true);
        WindowStyler.SetExStyleFlag(_hwnd, Win32.WS_EX_NOACTIVATE, true);

        // A bare popup with no frame at all. (Turning the border off through the presenter instead leaves an
        // old-style frame that Windows paints as a thick white outline.) No DWM border line or rounding either.
        const long FrameStyles = 0x00C00000 /*WS_CAPTION*/ | 0x00040000 /*WS_THICKFRAME*/ | 0x00080000 /*WS_SYSMENU*/ |
                                 0x00020000 /*WS_MINIMIZEBOX*/ | 0x00010000 /*WS_MAXIMIZEBOX*/;
        long style = Win32.GetWindowLongPtrW(_hwnd, Win32.GWL_STYLE);
        Win32.SetWindowLongPtrW(_hwnd, Win32.GWL_STYLE, (nint)((style & ~FrameStyles) | Win32.WS_POPUP));
        Win32.SetWindowPos(_hwnd, 0, 0, 0, 0, 0,
            Win32.SWP_NOMOVE | Win32.SWP_NOSIZE | Win32.SWP_NOZORDER | Win32.SWP_NOACTIVATE | Win32.SWP_FRAMECHANGED);
        Win32.SetDwmInt(_hwnd, Win32.DWMWA_WINDOW_CORNER_PREFERENCE, SquareCorners);
        Win32.SetDwmInt(_hwnd, DwmBorderColor, unchecked((int)DwmColorNone));

        var allPins = new MenuFlyoutItem { Text = "Turn off click-through for all pins" };
        allPins.Click += (_, _) => TurnOffAllRequested?.Invoke(this, EventArgs.Empty);
        var thisNote = new MenuFlyoutItem { Text = "Turn off for this note only" };
        thisNote.Click += (_, _) => TurnOffRequested?.Invoke(this, EventArgs.Empty);

        var dim = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"];
        var surface = new Grid
        {
            Background = (Brush)Application.Current.Resources["SolidBackgroundFillColorSecondaryBrush"],
            ContextFlyout = new MenuFlyout { Items = { allPins, thisNote } },
            Children =
            {
                new Grid
                {
                    Width = 14,
                    Height = 14,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    Children =
                    {
                        new FontIcon { Glyph = "", FontSize = 11, Foreground = dim },
                        new Line { X1 = 1, Y1 = 1, X2 = 13, Y2 = 13, Stroke = dim, StrokeThickness = 1.25 },
                    },
                },
            },
        };
        surface.Tapped += (_, _) => TurnOffAllRequested?.Invoke(this, EventArgs.Empty);
        surface.Loaded += (_, _) => DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            _rendered = true;
            if (IsShown) WindowStyler.SetAlpha(_hwnd, _alpha);
        });
        Content = surface;
    }

    /// <summary>Right-click → turn click-through off for just this badge's note.</summary>
    public event EventHandler? TurnOffRequested;

    /// <summary>Clicked: turn click-through off for every pin.</summary>
    public event EventHandler? TurnOffAllRequested;

    public bool IsShown { get; private set; }

    /// <summary>
    /// Shows the badge exactly on <paramref name="screenRect"/>, above everything, at the note's
    /// <paramref name="opacity"/> so a faded note doesn't get a solid patch on its header.
    /// </summary>
    public void ShowAt(PixelRect screenRect, double opacity)
    {
        _alpha = (byte)Math.Round(Math.Clamp(opacity, 0.1, 1.0) * 255);
        WindowStyler.SetAlpha(_hwnd, _rendered ? _alpha : (byte)0);
        AppWindow.Move(new PointInt32(screenRect.X, screenRect.Y)); // onto the right monitor before sizing
        WindowHelpers.SetClientScreenRect(_hwnd, AppWindow, screenRect);
        if (!IsShown) AppWindow.Show(activateWindow: false);
        WindowStyler.RaiseTopmost(_hwnd);
        IsShown = true;
    }

    public void HideBadge()
    {
        if (!IsShown) return;
        AppWindow.Hide();
        IsShown = false;
    }

    public void RaiseTopmost()
    {
        if (IsShown) WindowStyler.RaiseTopmost(_hwnd);
    }
}
