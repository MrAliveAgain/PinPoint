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
        SnipHotkeyText.Text = _host.Settings.GetHotkey(snip)?.ToDisplayString() ?? string.Empty;

        BuildShortcutHints();
        BuildMouseHints();
    }

    /// <summary>Mouse gestures handled by ImagePinWindow. Keep in sync with its pointer handlers.</summary>
    private static readonly (string[] Inputs, string Action)[] s_mouseControls =
    [
        (["Drag"], "Move it"),
        (["Scroll"], "Resize it"),
        (["Hold right-click", "Scroll"], "Fade it"),
        (["Double-click"], "Back to original size"),
        (["Middle-click"], "Close it"),
        (["Click T", "Drag over text"], "Copy text (the T button appears on hover when a snip has text)"),
        (["Right-click"], "Copy image or text, save as, opacity and more"),
    ];

    private void BuildMouseHints()
    {
        MouseHints.Children.Clear();
        foreach (var (inputs, action) in s_mouseControls)
            MouseHints.Children.Add(CreateHintRow(inputs, action, joinWithPlus: true));
    }

    private void BuildShortcutHints()
    {
        ShortcutHints.Children.Clear();
        foreach (var command in _host.Commands.All)
        {
            if (_host.Settings.GetHotkey(command) is not { } hotkey) continue;
            ShortcutHints.Children.Add(CreateHintRow(hotkey.DisplayParts, command.Title, joinWithPlus: false));
        }
    }

    /// <summary>A row of key caps followed by what they do. Mouse combos get a "+" between caps.</summary>
    private static StackPanel CreateHintRow(IReadOnlyList<string> caps, string action, bool joinWithPlus)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        for (int i = 0; i < caps.Count; i++)
        {
            if (joinWithPlus && i > 0)
            {
                row.Children.Add(new TextBlock
                {
                    Text = "+",
                    Margin = new Thickness(2, 0, 2, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                    Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
                });
            }

            row.Children.Add(new Border
            {
                Style = (Style)Application.Current.Resources["KeyCapStyle"],
                Child = new TextBlock
                {
                    Text = caps[i],
                    FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                    Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
                    HorizontalAlignment = HorizontalAlignment.Center,
                },
            });
        }

        row.Children.Add(new TextBlock
        {
            Text = action,
            Margin = new Thickness(8, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
        });
        return row;
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

    private void CopyText_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: PinItem item }) item.CopyText();
    }
}
