using System.Collections.Specialized;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using PinPoint.Commands;
using PinPoint.Core.Windows;
using PinPoint.ViewModels;

namespace PinPoint.Views;

public sealed partial class PinsPage : Page
{
    private readonly AppHost _host = AppHost.Current;

    public PinsPage()
    {
        InitializeComponent();
        PinList.ItemsSource = _host.Pins.Items;

        Loaded += (_, _) =>
        {
            _host.Pins.StateChanged += OnPinsChanged;
            _host.Pins.Items.CollectionChanged += OnItemsChanged;
            _host.HotkeysChanged += OnPinsChanged;
            Refresh();
        };
        Unloaded += (_, _) =>
        {
            _host.Pins.StateChanged -= OnPinsChanged;
            _host.Pins.Items.CollectionChanged -= OnItemsChanged;
            _host.HotkeysChanged -= OnPinsChanged;
        };
    }

    private void OnPinsChanged(object? sender, EventArgs e) => Refresh();

    private void OnItemsChanged(object? sender, NotifyCollectionChangedEventArgs e) => Refresh();

    private void Refresh()
    {
        var pins = _host.Pins;
        EmptyState.Visibility = pins.HasPins ? Visibility.Collapsed : Visibility.Visible;
        PinList.Visibility = pins.HasPins ? Visibility.Visible : Visibility.Collapsed;

        HideAllToggle.IsEnabled = ClickThroughToggle.IsEnabled = pins.HasPins;
        HideAllToggle.IsChecked = pins.IsHidden;
        ClickThroughToggle.IsChecked = pins.IsClickThrough;

        var snip = _host.Commands.Get(CommandIds.SnipAndPin);
        SnipHotkeyText.Text = _host.Settings.GetHotkey(snip)?.ToString() ?? string.Empty;

        EmptyStateText.Text =
            "Pins float above everything — including games in borderless windowed mode. Drag them anywhere and scroll to resize. " +
            "To fade a snip, hold the right mouse button and scroll.";

        BuildShortcutHints();
    }

    private void BuildShortcutHints()
    {
        ShortcutHints.Children.Clear();
        foreach (var command in _host.Commands.All)
        {
            if (_host.Settings.GetHotkey(command) is not { } hotkey) continue;

            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
            foreach (string part in hotkey.Parts)
            {
                row.Children.Add(new Border
                {
                    Style = (Style)Application.Current.Resources["KeyCapStyle"],
                    Child = new TextBlock
                    {
                        Text = part,
                        FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                        Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
                    },
                });
            }

            row.Children.Add(new TextBlock
            {
                Text = command.Title,
                Margin = new Thickness(8, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
            });
            ShortcutHints.Children.Add(row);
        }
    }

    private async void Snip_Click(object sender, RoutedEventArgs e)
    {
        // Get our own window out of the shot first.
        if (XamlRoot?.ContentIslandEnvironment?.AppWindowId is { } id &&
            Microsoft.UI.Windowing.AppWindow.GetFromWindowId(id)?.Presenter is Microsoft.UI.Windowing.OverlappedPresenter presenter)
        {
            presenter.Minimize();
            await Task.Delay(250); // let the minimise animation finish
        }

        _host.Snips.Start();
    }

    private void WindowMenu_Opening(object sender, object e)
    {
        WindowMenu.Items.Clear();
        var windows = WindowEnumerator.GetPinnableWindows();

        if (windows.Count == 0)
        {
            WindowMenu.Items.Add(new MenuFlyoutItem { Text = "No windows found", IsEnabled = false });
            return;
        }

        foreach (var window in windows)
        {
            var item = new ToggleMenuFlyoutItem
            {
                Text = window.Title.Length > 70 ? window.Title[..67] + "…" : window.Title,
                IsChecked = _host.Pins.IsWindowPinned(window.Handle),
            };
            ToolTipService.SetToolTip(item, window.ProcessName);

            nint handle = window.Handle;
            item.Click += (_, _) =>
            {
                if (_host.Pins.IsWindowPinned(handle)) _host.Pins.UnpinWindow(handle);
                else _host.Pins.PinWindow(handle);
            };
            WindowMenu.Items.Add(item);
        }
    }

    private void HideAll_Click(object sender, RoutedEventArgs e) => _host.Pins.ToggleHidden();

    private void ClickThrough_Click(object sender, RoutedEventArgs e) => _host.Pins.ToggleClickThrough();

    private void Remove_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: PinItem item }) item.Remove();
    }
}
