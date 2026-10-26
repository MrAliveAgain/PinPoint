using System.Collections.Specialized;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using PinPoint.Commands;
using PinPoint.Core.Windows;
using PinPoint.ViewModels;
using Windows.ApplicationModel.DataTransfer;

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
        (["Drag"], "Move"),
        (["Scroll"], "Resize"),
        (["Hold right-click", "Scroll"], "Fade"),
        (["Double-click"], "Original size"),
        (["Middle-click"], "Close"),
        (["Click T", "Drag over text"], "Copy text (T shows on hover)"),
        (["Click pen", "Drag"], "Draw · right-drag erases"),
        (["Right-click"], "Copy, save, opacity, lock and more"),
        (["Drop image files here"], "Pin them like snips"),
    ];

    // ---- Drop image files (or a picture from a browser) onto the window to pin them -------------

    private void Page_DragOver(object sender, DragEventArgs e)
    {
        if (!e.DataView.Contains(StandardDataFormats.StorageItems) && !e.DataView.Contains(StandardDataFormats.Bitmap)) return;
        e.AcceptedOperation = DataPackageOperation.Copy;
        e.DragUIOverride.Caption = "Pin on top";
    }

    private async void Page_Drop(object sender, DragEventArgs e)
    {
        var deferral = e.GetDeferral();
        try
        {
            if (await _host.Snips.PinFromDataAsync(e.DataView) == 0)
                _host.Notifier.Show("", "Nothing to pin", "Only images can be pinned (PNG, JPEG, GIF, WebP…).", force: true);
        }
        catch (Exception ex)
        {
            Core.Diagnostics.Log.Error("Pinning dropped images failed", ex);
        }
        finally
        {
            deferral.Complete();
        }
    }

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

    /// <summary>
    /// A row of key caps followed by what they do (wrapping, never cut off, if the window is narrow).
    /// Mouse combos get a "+" between caps.
    /// </summary>
    private static Grid CreateHintRow(IReadOnlyList<string> caps, string action, bool joinWithPlus)
    {
        var keys = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, VerticalAlignment = VerticalAlignment.Top };
        for (int i = 0; i < caps.Count; i++)
        {
            if (joinWithPlus && i > 0)
            {
                keys.Children.Add(new TextBlock
                {
                    Text = "+",
                    Margin = new Thickness(2, 0, 2, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                    Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
                });
            }

            keys.Children.Add(new Border
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

        var text = new TextBlock
        {
            Text = action,
            Margin = new Thickness(12, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
        };
        Grid.SetColumn(text, 1);

        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.Children.Add(keys);
        row.Children.Add(text);
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

    private void NewNote_Click(object sender, RoutedEventArgs e) => _host.Pins.CreateNote();

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
