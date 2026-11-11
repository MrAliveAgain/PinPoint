using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Media.Imaging;
using PinPoint.Core.Interop;
using PinPoint.Core.Windows;
using PinPoint.Helpers;
using Windows.Graphics;

namespace PinPoint.Views;

/// <summary>The main window (Pins + Settings). Created on demand and destroyed when closed to keep idle memory low.</summary>
public sealed partial class ManagerWindow : Window
{
    public const string PinsPageTag = "pins";
    public const string BoardsPageTag = "boards";
    public const string SettingsPageTag = "settings";

    private readonly nint _hwnd;

    public ManagerWindow()
    {
        InitializeComponent();
        _hwnd = this.GetHandle();

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Tall;
        AppWindow.SetIcon(WindowHelpers.IconPath);
        TitleBarIcon.Source = new BitmapImage(new Uri(Path.Combine(AppContext.BaseDirectory, "Assets", "PinPoint.png")));

        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.PreferredMinimumWidth = 640;
            presenter.PreferredMinimumHeight = 480;
        }

        CenterOnCursorMonitor(width: 960, height: 700);
        Navigate(PinsPageTag);
        Activated += (_, e) => KeepAbovePins(e.WindowActivationState != WindowActivationState.Deactivated);
    }

    /// <summary>
    /// Pins are always on top, so they used to cover this window even after Alt+Tab or opening it from the tray.
    /// While it's the active window it joins the always-on-top band (above the pins); when you switch away it
    /// drops back to a normal window, tucked just under whatever you switched to rather than over it.
    /// </summary>
    private void KeepAbovePins(bool active)
    {
        if (active)
        {
            WindowStyler.SetTopmost(_hwnd, true);
            return;
        }

        WindowStyler.SetTopmost(_hwnd, false);
        nint foreground = Win32.GetForegroundWindow();
        if (foreground != 0 && foreground != _hwnd && !WindowStyler.HasExStyle(foreground, Win32.WS_EX_TOPMOST))
        {
            Win32.SetWindowPos(_hwnd, foreground, 0, 0, 0, 0,
                Win32.SWP_NOMOVE | Win32.SWP_NOSIZE | Win32.SWP_NOACTIVATE | Win32.SWP_NOOWNERZORDER);
        }
    }

    public void ShowAndActivate(string? page)
    {
        if (page is not null) Navigate(page);
        if (AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized } presenter) presenter.Restore();
        Activate();
        Win32.SetForegroundWindow(_hwnd);
    }

    public void Navigate(string tag)
    {
        var pageType = tag switch
        {
            SettingsPageTag => typeof(SettingsPage),
            BoardsPageTag => typeof(BoardsPage),
            _ => typeof(PinsPage),
        };
        if (ContentFrame.CurrentSourcePageType != pageType)
            ContentFrame.Navigate(pageType, null, new EntranceNavigationTransitionInfo());

        Nav.SelectedItem = tag == SettingsPageTag
            ? Nav.SettingsItem
            : Nav.MenuItems.OfType<NavigationViewItem>().FirstOrDefault(i => (string)i.Tag == tag) ?? Nav.MenuItems[0];
    }

    private void Nav_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args) =>
        Navigate(args.IsSettingsSelected ? SettingsPageTag : (args.SelectedItem as NavigationViewItem)?.Tag as string ?? PinsPageTag);

    private void CenterOnCursorMonitor(int width, int height)
    {
        Win32.GetCursorPos(out var cursor);
        var (_, work) = WindowEnumerator.GetMonitorAt(cursor.X, cursor.Y);
        AppWindow.Move(new PointInt32(work.X + 40, work.Y + 40)); // pick up that monitor's DPI first
        double scale = WindowHelpers.GetScale(_hwnd);
        int w = Math.Min((int)(width * scale), work.Width);
        int h = Math.Min((int)(height * scale), work.Height);
        AppWindow.MoveAndResize(new RectInt32(work.X + (work.Width - w) / 2, work.Y + (work.Height - h) / 2, w, h));
    }
}
