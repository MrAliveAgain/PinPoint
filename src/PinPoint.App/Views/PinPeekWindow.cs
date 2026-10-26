using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using PinPoint.Core.Geometry;
using PinPoint.Core.Interop;
using PinPoint.Core.Windows;
using PinPoint.Helpers;
using Windows.Graphics;
using Windows.UI;

namespace PinPoint.Views;

/// <summary>
/// The full-size look at a pin while hovering it in a board preview. A separate always-on-top window so it
/// can be bigger than the preview, and fully click-through (layered + transparent) so the mouse stays on
/// the pin underneath: moving off that pin is what hides it again. Never takes focus.
/// </summary>
public sealed class PinPeekWindow : Window
{
    private readonly nint _hwnd;
    private readonly Image _image = new() { Stretch = Stretch.Fill };

    public PinPeekWindow()
    {
        _hwnd = this.GetHandle();
        WindowHelpers.MakeToolSurface(AppWindow, alwaysOnTop: true);
        WindowStyler.SetExStyleFlag(_hwnd, Win32.WS_EX_TOOLWINDOW, true);
        WindowStyler.SetExStyleFlag(_hwnd, Win32.WS_EX_NOACTIVATE, true);
        WindowStyler.SetExStyleFlag(_hwnd, Win32.WS_EX_TRANSPARENT, true);
        WindowStyler.SetAlpha(_hwnd, 255); // layered, so WS_EX_TRANSPARENT really lets the mouse through

        var accent = Application.Current.Resources.TryGetValue("SystemAccentColor", out var value) && value is Color c
            ? c
            : Color.FromArgb(255, 76, 194, 255);
        Content = new Border
        {
            BorderThickness = new Thickness(2),
            BorderBrush = new SolidColorBrush(accent),
            Child = _image,
        };
    }

    /// <summary>Shows <paramref name="image"/> with its picture area exactly on <paramref name="screenRect"/>.</summary>
    public void ShowAt(ImageSource image, PixelRect screenRect)
    {
        _image.Source = image;
        AppWindow.Move(new PointInt32(screenRect.X, screenRect.Y)); // onto the right monitor before sizing
        WindowHelpers.SetClientScreenRect(_hwnd, AppWindow, screenRect);
        AppWindow.Show(activateWindow: false);
        WindowStyler.RaiseTopmost(_hwnd);
    }

    public void HidePeek()
    {
        AppWindow.Hide();
        _image.Source = null;
    }
}
