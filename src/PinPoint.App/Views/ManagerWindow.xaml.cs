using Microsoft.UI.Composition;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Media.Imaging;
using PinPoint.Core.Interop;
using PinPoint.Core.Windows;
using PinPoint.Helpers;
using Windows.Graphics;
using WinRT;

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

        Root.ActualThemeChanged += (_, _) => UpdateBackdropTheme();
        ApplyAppearance();
        var settings = AppHost.Current.Settings;
        settings.Changed += OnSettingsChanged;
        Closed += (_, _) =>
        {
            settings.Changed -= OnSettingsChanged;
            _acrylic?.Dispose();
            _acrylic = null;
            _backdropConfig = null;
        };

        // The selected nav item would otherwise open wearing the keyboard focus rectangle (a hard white box).
        Activated += (_, e) =>
        {
            if (e.WindowActivationState == WindowActivationState.Deactivated) return;
            DispatcherQueue.TryEnqueue(() =>
            {
                // Activation parks keyboard focus on the nav item (the white box). Hand it to the page's first control
                // as if clicked instead: nothing shows a focus rectangle until you actually use the keyboard.
                if (Content?.XamlRoot is { } root && Microsoft.UI.Xaml.Input.FocusManager.GetFocusedElement(root) is not NavigationViewItem) return;
                if (Microsoft.UI.Xaml.Input.FocusManager.FindFirstFocusableElement(ContentFrame) is UIElement first)
                    _ = Microsoft.UI.Xaml.Input.FocusManager.TryFocusAsync(first, FocusState.Pointer);
            });
        };
        Activated += (_, e) => KeepAbovePins(e.WindowActivationState != WindowActivationState.Deactivated);

        // Minimize to the tray: out of the taskbar and Alt+Tab. The window is closed, not just hidden: its pages,
        // thumbnails and glass would otherwise sit in memory the whole time PinPoint lives in the tray. The tray
        // rebuilds it in a moment when you open it again.
        AppWindow.Changed += (_, e) =>
        {
            if (!AppHost.Current.Settings.Current.MinimizeToTray || _closingToTray) return;
            if (AppWindow.IsVisible && AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized })
            {
                _closingToTray = true;
                AppWindow.Hide();
                DispatcherQueue.TryEnqueue(Close);
            }
        };
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

    private bool _closingToTray;

    /// <summary>Closed because it was minimized to the tray (no "still running" hint needed).</summary>
    public bool ClosedToTray => _closingToTray;

    private Core.Settings.AppearanceStyle? _appliedAppearance;
    private DesktopAcrylicController? _acrylic;
    private SystemBackdropConfiguration? _backdropConfig;

    private void OnSettingsChanged(object? sender, EventArgs e) => DispatcherQueue.TryEnqueue(ApplyAppearance);

    /// <summary>Glass: desktop acrylic (what's behind shows through, blurred). Subtle: Mica Alt. Solid: none.</summary>
    private void ApplyAppearance()
    {
        var style = AppHost.Current.Settings.Current.Appearance;
        if (style == _appliedAppearance) return;
        _appliedAppearance = style;

        _acrylic?.Dispose();
        _acrylic = null;
        _backdropConfig = null;
        SystemBackdrop = style == Core.Settings.AppearanceStyle.Subtle
            ? new Microsoft.UI.Xaml.Media.MicaBackdrop { Kind = MicaKind.BaseAlt }
            : null;

        if (style == Core.Settings.AppearanceStyle.Glass && DesktopAcrylicController.IsSupported())
        {
            _backdropConfig = new SystemBackdropConfiguration { IsInputActive = true };
            _acrylic = new DesktopAcrylicController { Kind = DesktopAcrylicKind.Base };
            UpdateBackdropTheme();
            _acrylic.AddSystemBackdropTarget(this.As<ICompositionSupportsSystemBackdrop>());
            _acrylic.SetSystemBackdropConfiguration(_backdropConfig);
        }

        // Solid needs an actual surface under everything; the materials want it clear.
        Root.Background = style == Core.Settings.AppearanceStyle.Solid
            ? (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["SolidBackgroundFillColorBaseBrush"]
            : null;
    }

    private void UpdateBackdropTheme()
    {
        if (_backdropConfig is null || _acrylic is null) return;

        bool dark = Root.ActualTheme == ElementTheme.Dark;
        _backdropConfig.Theme = dark ? SystemBackdropTheme.Dark : SystemBackdropTheme.Light;
        _acrylic.TintColor = dark
            ? Windows.UI.Color.FromArgb(255, 0x20, 0x22, 0x26)
            : Windows.UI.Color.FromArgb(255, 0xF3, 0xF3, 0xF3);
        _acrylic.TintOpacity = 0.65f;
        _acrylic.LuminosityOpacity = 0.85f;
    }

    public void ShowAndActivate(string? page)
    {
        if (page is not null) Navigate(page);
        if (AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized } presenter) presenter.Restore();
        if (!AppWindow.IsVisible) AppWindow.Show(activateWindow: true); // was tucked into the tray
        Activate();
        // A plain SetForegroundWindow is often refused (Windows' foreground lock, e.g. coming from a game or the
        // tray), which left the window behind your pins. Force it, and lift it above the pins right away rather
        // than waiting for an activation that may never come.
        KeepAbovePins(true);
        Win32.ForceForeground(_hwnd);
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
