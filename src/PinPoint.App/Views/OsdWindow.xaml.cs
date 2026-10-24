using Microsoft.UI.Composition;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using PinPoint.Core.Interop;
using PinPoint.Core.Windows;
using PinPoint.Helpers;
using Windows.Foundation;
using Windows.Graphics;
using WinRT;

namespace PinPoint.Views;

/// <summary>
/// Small acrylic toast near the top of the active monitor ("Pinned on top"). Never takes focus,
/// so it is safe to show over a game. One instance is reused for every message.
/// </summary>
public sealed partial class OsdWindow : Window
{
    private static readonly TimeSpan VisibleFor = TimeSpan.FromMilliseconds(1600);

    private readonly nint _hwnd;
    private readonly DispatcherQueueTimer _hideTimer;
    private readonly DispatcherQueueTimer _fadeTimer;
    private DesktopAcrylicController? _acrylic;
    private SystemBackdropConfiguration? _backdropConfig;

    public OsdWindow()
    {
        InitializeComponent();
        _hwnd = this.GetHandle();

        WindowHelpers.MakeToolSurface(AppWindow, alwaysOnTop: true);
        WindowStyler.SetExStyleFlag(_hwnd, Win32.WS_EX_NOACTIVATE | Win32.WS_EX_TOOLWINDOW, true);
        SetupBackdrop();

        _hideTimer = DispatcherQueue.CreateTimer();
        _hideTimer.IsRepeating = false;
        _hideTimer.Interval = VisibleFor;
        _hideTimer.Tick += (_, _) =>
        {
            Root.Opacity = 0;
            _fadeTimer!.Start();
        };

        _fadeTimer = DispatcherQueue.CreateTimer();
        _fadeTimer.IsRepeating = false;
        _fadeTimer.Interval = TimeSpan.FromMilliseconds(180);
        _fadeTimer.Tick += (_, _) => AppWindow.Hide();

        Closed += (_, _) => _acrylic?.Dispose();
    }

    public void Show(string glyph, string title, string? detail)
    {
        GlyphIcon.Glyph = glyph;
        TitleText.Text = title;
        DetailText.Text = detail ?? string.Empty;
        DetailText.Visibility = string.IsNullOrEmpty(detail) ? Visibility.Collapsed : Visibility.Visible;

        // Top-centre of the monitor under the mouse (that's where the user is looking).
        Win32.GetCursorPos(out var cursor);
        var (_, work) = WindowEnumerator.GetMonitorAt(cursor.X, cursor.Y);

        // Move first so the DPI is that monitor's, then size to content.
        AppWindow.Move(new PointInt32(work.X + work.Width / 2, work.Y + 40));
        double scale = WindowHelpers.GetScale(_hwnd);
        Root.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        // Size the client area (not the whole window: its border would clip the text), plus a little slack
        // for text rounding.
        int width = (int)Math.Ceiling((Root.DesiredSize.Width + 2) * scale);
        int height = (int)Math.Ceiling((Math.Max(Root.DesiredSize.Height, 44) + 2) * scale);
        AppWindow.ResizeClient(new SizeInt32(width, height));
        AppWindow.Move(new PointInt32(work.X + (work.Width - AppWindow.Size.Width) / 2, work.Y + (int)(36 * scale)));

        _fadeTimer.Stop();
        _hideTimer.Stop();
        AppWindow.Show(activateWindow: false);
        WindowStyler.RaiseTopmost(_hwnd);
        Root.Opacity = 1;
        _hideTimer.Start();
    }

    private void SetupBackdrop()
    {
        if (!DesktopAcrylicController.IsSupported()) return;

        // Drive acrylic manually so it stays "active" even though this window never gets focus.
        _backdropConfig = new SystemBackdropConfiguration { IsInputActive = true };
        UpdateBackdropTheme();
        Root.ActualThemeChanged += (_, _) => UpdateBackdropTheme();

        _acrylic = new DesktopAcrylicController();
        _acrylic.AddSystemBackdropTarget(this.As<ICompositionSupportsSystemBackdrop>());
        _acrylic.SetSystemBackdropConfiguration(_backdropConfig);
    }

    private void UpdateBackdropTheme()
    {
        if (_backdropConfig is null) return;
        _backdropConfig.Theme = Root.ActualTheme == ElementTheme.Dark ? SystemBackdropTheme.Dark : SystemBackdropTheme.Light;
    }
}
