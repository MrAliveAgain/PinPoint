using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using PinPoint.Core.Geometry;
using PinPoint.Core.Interop;
using PinPoint.Core.Windows;
using PinPoint.Helpers;
using Windows.Graphics;

namespace PinPoint.Views;

/// <summary>
/// A small cursor button that floats over a click-through note. The note itself lets every click through
/// (that's the point), so this separate window is the one spot you can still click: left-click turns
/// click-through off for every pin, right-click also offers just that note. Never takes focus,
/// so clicking it doesn't pull you out of a game.
/// </summary>
public sealed class ClickThroughBadge : Window
{
    private readonly nint _hwnd;

    public ClickThroughBadge()
    {
        _hwnd = this.GetHandle();
        WindowHelpers.MakeToolSurface(AppWindow, alwaysOnTop: true);
        WindowStyler.SetExStyleFlag(_hwnd, Win32.WS_EX_TOOLWINDOW, true);
        WindowStyler.SetExStyleFlag(_hwnd, Win32.WS_EX_NOACTIVATE, true);

        var allPins = new MenuFlyoutItem { Text = "Turn off click-through for all pins" };
        allPins.Click += (_, _) => TurnOffAllRequested?.Invoke(this, EventArgs.Empty);
        var thisNote = new MenuFlyoutItem { Text = "Turn off for this note only" };
        thisNote.Click += (_, _) => TurnOffRequested?.Invoke(this, EventArgs.Empty);

        var button = new Button
        {
            Style = (Style)Application.Current.Resources["AccentButtonStyle"],
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            Padding = new Thickness(0),
            CornerRadius = new CornerRadius(0),
            BorderThickness = new Thickness(0),
            Content = new FontIcon { Glyph = "", FontSize = 12 },
            ContextFlyout = new MenuFlyout { Items = { allPins, thisNote } },
        };
        ToolTipService.SetToolTip(button, "Click-through is on. Click to turn it off for all pins (right-click: just this note).");
        button.Click += (_, _) => TurnOffAllRequested?.Invoke(this, EventArgs.Empty);
        Content = button;
    }

    /// <summary>Clicked: turn click-through off for the note this badge belongs to.</summary>
    public event EventHandler? TurnOffRequested;

    /// <summary>Right-click → turn click-through off for every pin.</summary>
    public event EventHandler? TurnOffAllRequested;

    public bool IsShown { get; private set; }

    /// <summary>Shows the badge with its button exactly on <paramref name="screenRect"/>, above everything.</summary>
    public void ShowAt(PixelRect screenRect)
    {
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
