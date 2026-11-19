using Microsoft.UI.Composition;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Xaml;
using WinRT;

namespace PinPoint.Helpers;

/// <summary>
/// Real desktop glass for a pin window (notes, timers): what's behind it shows through, blurred and tinted, and
/// it stays "active" glass even though pins never take focus. Windows can't draw a system backdrop on a window
/// it is fading (a layered window), so whenever the pin is faded below 100% or click-through, this hands back
/// to the solid look; it follows Settings > Appearance (Glass only) as well.
/// </summary>
public sealed class PinGlass : IDisposable
{
    private readonly Window _window;
    private readonly FrameworkElement _themeSource;
    private readonly Action<bool> _applySurface;
    private DesktopAcrylicController? _acrylic;
    private SystemBackdropConfiguration? _config;
    private bool? _glass;

    /// <param name="applySurface">Called with true for glass (make backgrounds see-through), false for solid.</param>
    public PinGlass(Window window, FrameworkElement themeSource, Action<bool> applySurface)
    {
        _window = window;
        _themeSource = themeSource;
        _applySurface = applySurface;
        themeSource.ActualThemeChanged += (_, _) => UpdateTheme();
        AppHost.Current.Settings.Changed += OnSettingsChanged;
    }

    private bool _fadedOrThrough;

    /// <summary>Re-evaluates glass vs solid: call when opacity or click-through changes.</summary>
    public void Update(bool fadedOrClickThrough)
    {
        _fadedOrThrough = fadedOrClickThrough;
        bool want = !fadedOrClickThrough &&
                    AppHost.Current.Settings.Current.Appearance == Core.Settings.AppearanceStyle.Glass &&
                    DesktopAcrylicController.IsSupported();
        if (want == _glass) return;
        _glass = want;

        if (want)
        {
            _config = new SystemBackdropConfiguration { IsInputActive = true }; // glass even when not focused
            UpdateTheme();
            _acrylic = new DesktopAcrylicController { Kind = DesktopAcrylicKind.Base }; // the denser glass: text stays easy to read
            _acrylic.AddSystemBackdropTarget(_window.As<ICompositionSupportsSystemBackdrop>());
            _acrylic.SetSystemBackdropConfiguration(_config);
        }
        else
        {
            _acrylic?.RemoveAllSystemBackdropTargets();
            _acrylic?.Dispose();
            _acrylic = null;
        }

        _applySurface(want);
    }

    private void OnSettingsChanged(object? sender, EventArgs e) =>
        _window.DispatcherQueue.TryEnqueue(() => Update(_fadedOrThrough));

    private void UpdateTheme()
    {
        if (_config is null) return;
        _config.Theme = _themeSource.ActualTheme == ElementTheme.Dark ? SystemBackdropTheme.Dark : SystemBackdropTheme.Light;
    }

    public void Dispose()
    {
        AppHost.Current.Settings.Changed -= OnSettingsChanged;
        _acrylic?.Dispose();
        _acrylic = null;
    }
}
