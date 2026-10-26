using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using PinPoint.Core.Boards;
using PinPoint.Core.Diagnostics;
using PinPoint.Core.Interop;
using PinPoint.Core.Windows;
using PinPoint.Helpers;
using PinPoint.Services;
using Windows.Graphics;
using Windows.UI;
using VirtualKey = Windows.System.VirtualKey;

namespace PinPoint.Views;

/// <summary>
/// A big look at a saved board without opening it: every pin as it was on screen, or packed together.
/// Hover a pin for a moment to see it at full size; right-click it to copy or save just that pin.
/// Open and Export act on the whole board. Built in code: it's one window with a handful of parts.
/// </summary>
public sealed class BoardPreviewWindow : Window
{
    private static readonly TimeSpan HoverDelay = TimeSpan.FromMilliseconds(400);

    /// <summary>Space around the pins, in screen pixels.</summary>
    private const int StageMargin = 32;

    private readonly AppHost _host = AppHost.Current;
    private readonly string _boardId;
    private readonly Func<Task> _open;
    private readonly nint _hwnd;
    private readonly bool _dark = Application.Current.RequestedTheme == ApplicationTheme.Dark;

    private readonly Grid _stage = new();
    private readonly Canvas _canvas = new();
    private readonly Canvas _overlay = new() { IsHitTestVisible = false };
    private readonly Border _dim = new() { Visibility = Visibility.Collapsed };
    private readonly Border _enlarged = new() { Visibility = Visibility.Collapsed, CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1) };
    private readonly Image _enlargedImage = new() { Stretch = Stretch.Fill };
    private readonly ProgressRing _loading = new() { IsActive = true, Width = 40, Height = 40 };
    private readonly TextBlock _empty = new() { Text = "This board is empty.", Visibility = Visibility.Collapsed };
    private readonly TextBlock _status = new() { VerticalAlignment = VerticalAlignment.Center, Opacity = 0.8 };
    private readonly ToggleSwitch _packToggle;
    private readonly DispatcherQueueTimer _hoverTimer;
    private readonly DispatcherQueueTimer _statusTimer;

    private List<(PreviewPin Pin, BitmapImage Image)> _pins = [];
    private FrameworkElement? _hovered;

    public BoardPreviewWindow(string boardId, string name, Func<Task> open)
    {
        _boardId = boardId;
        _open = open;
        _hwnd = this.GetHandle();

        Title = $"{name} · PinPoint";
        SystemBackdrop = new MicaBackdrop();
        AppWindow.SetIcon(WindowHelpers.IconPath);

        _packToggle = new ToggleSwitch
        {
            OnContent = "Packed together",
            OffContent = "As on screen",
            IsOn = _host.Settings.Current.PreviewPacked,
            MinWidth = 0,
            VerticalAlignment = VerticalAlignment.Center,
        };
        _packToggle.Toggled += (_, _) =>
        {
            _host.Settings.Update(s => s.PreviewPacked = _packToggle.IsOn);
            LayOut();
        };

        _hoverTimer = DispatcherQueue.CreateTimer();
        _hoverTimer.Interval = HoverDelay;
        _hoverTimer.IsRepeating = false;
        _hoverTimer.Tick += (_, _) => ShowEnlarged();

        _statusTimer = DispatcherQueue.CreateTimer();
        _statusTimer.Interval = TimeSpan.FromSeconds(3);
        _statusTimer.IsRepeating = false;
        _statusTimer.Tick += (_, _) => _status.Text = string.Empty;

        Content = BuildContent(name);
        SizeToCursorMonitor();
        Closed += (_, _) => _hoverTimer.Stop();
        _ = LoadAsync();
    }

    // ---- Building ----------------------------------------------------------------------------

    private Grid BuildContent(string name)
    {
        var open = new Button { Content = "Open board", Style = (Style)Application.Current.Resources["AccentButtonStyle"] };
        open.Click += async (_, _) =>
        {
            Close();
            await _open();
        };

        var export = new Button { Content = "Export image…" };
        export.Click += async (_, _) => await ExportAsync();

        var header = new Grid { ColumnSpacing = 12, Padding = new Thickness(20, 12, 20, 4) };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        for (int i = 0; i < 4; i++) header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var title = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        title.Children.Add(new TextBlock { Text = name, Style = (Style)Application.Current.Resources["SubtitleTextBlockStyle"], TextTrimming = TextTrimming.CharacterEllipsis });
        title.Children.Add(new TextBlock
        {
            Text = "Hover a pin to see it full size · right-click it to copy or save it",
            Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
            Opacity = 0.75,
        });
        header.Children.Add(title);
        Place(header, _status, 1);
        Place(header, _packToggle, 2);
        Place(header, export, 3);
        Place(header, open, 4);

        _enlarged.Child = _enlargedImage;
        _enlarged.BorderBrush = new SolidColorBrush(_dark ? Color.FromArgb(80, 255, 255, 255) : Color.FromArgb(50, 0, 0, 0));
        _dim.Background = new SolidColorBrush(Color.FromArgb(_dark ? (byte)140 : (byte)90, 0, 0, 0));
        _overlay.Children.Add(_dim);
        _overlay.Children.Add(_enlarged);

        var viewbox = new Viewbox { Stretch = Stretch.Uniform, StretchDirection = StretchDirection.DownOnly, Child = _canvas, Margin = new Thickness(8) };
        _loading.HorizontalAlignment = _empty.HorizontalAlignment = HorizontalAlignment.Center;
        _loading.VerticalAlignment = _empty.VerticalAlignment = VerticalAlignment.Center;
        _stage.Children.Add(viewbox);
        _stage.Children.Add(_loading);
        _stage.Children.Add(_empty);
        _stage.Children.Add(_overlay);
        _stage.SizeChanged += (_, _) => HideEnlarged();
        Grid.SetRow(_stage, 1);

        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.Children.Add(header);
        root.Children.Add(_stage);

        var escape = new KeyboardAccelerator { Key = VirtualKey.Escape };
        escape.Invoked += (_, e) =>
        {
            e.Handled = true;
            Close();
        };
        root.KeyboardAccelerators.Add(escape);
        return root;

        static void Place(Grid grid, FrameworkElement element, int column)
        {
            Grid.SetColumn(element, column);
            grid.Children.Add(element);
        }
    }

    /// <summary>80% of the monitor the mouse is on, centred.</summary>
    private void SizeToCursorMonitor()
    {
        Win32.GetCursorPos(out var cursor);
        var (_, work) = WindowEnumerator.GetMonitorAt(cursor.X, cursor.Y);
        AppWindow.Move(new PointInt32(work.X + 40, work.Y + 40)); // pick up that monitor's DPI first
        int w = work.Width * 4 / 5, h = work.Height * 4 / 5;
        AppWindow.MoveAndResize(new RectInt32(work.X + (work.Width - w) / 2, work.Y + (work.Height - h) / 2, w, h));
    }

    private async Task LoadAsync()
    {
        try
        {
            if (await _host.Boards.GetPreviewAsync(_boardId) is { } preview)
            {
                var pins = new List<(PreviewPin, BitmapImage)>();
                foreach (var pin in preview.Pins)
                {
                    var bitmap = new BitmapImage();
                    using (var stream = new MemoryStream(pin.Png)) await bitmap.SetSourceAsync(stream.AsRandomAccessStream());
                    pins.Add((pin, bitmap));
                }

                _pins = pins;
            }
        }
        catch (Exception ex)
        {
            Log.Error($"Could not load preview of board {_boardId}", ex);
        }

        _loading.IsActive = false;
        _loading.Visibility = Visibility.Collapsed;
        LayOut();
    }

    /// <summary>Places every pin on the canvas, in DIPs, so it shows at most at its real size.</summary>
    private void LayOut()
    {
        HideEnlarged();
        _canvas.Children.Clear();

        var items = BoardService.Arrange([.. _pins.Select(p => p.Pin.Item)], _packToggle.IsOn);
        if (CollageLayout.Fit(items.Select(i => i.Bounds), StageMargin, int.MaxValue, int.MaxValue) is not { } layout)
        {
            _empty.Visibility = _loading.IsActive ? Visibility.Collapsed : Visibility.Visible;
            return;
        }

        double scale = WindowHelpers.GetScale(_hwnd);
        _canvas.Width = layout.Width / scale;
        _canvas.Height = layout.Height / scale;

        var stroke = new SolidColorBrush(_dark ? Color.FromArgb(51, 255, 255, 255) : Color.FromArgb(31, 0, 0, 0));
        for (int i = 0; i < items.Count; i++)
        {
            var (x, y, width, height) = layout.Map(items[i].Bounds);
            var element = new Border
            {
                Width = width / scale,
                Height = height / scale,
                CornerRadius = new CornerRadius(8),
                BorderThickness = new Thickness(1),
                BorderBrush = stroke,
                Child = new Image { Source = _pins[i].Image, Stretch = Stretch.Fill },
                Tag = i,
                ContextFlyout = PinMenu(i),
            };
            Canvas.SetLeft(element, x / scale);
            Canvas.SetTop(element, y / scale);
            element.PointerEntered += Pin_PointerEntered;
            element.PointerExited += Pin_PointerExited;
            _canvas.Children.Add(element);
        }
    }

    // ---- Hover to enlarge --------------------------------------------------------------------

    private void Pin_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        _hovered = (FrameworkElement)sender;
        _hoverTimer.Stop();
        _hoverTimer.Start();
    }

    private void Pin_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (!ReferenceEquals(_hovered, sender)) return;
        _hovered = null;
        _hoverTimer.Stop();
        HideEnlarged();
    }

    /// <summary>Shows the hovered pin at its on-screen size (shrunk only if the window is smaller), centred over it.</summary>
    private void ShowEnlarged()
    {
        if (_hovered is not { Tag: int index } element || index >= _pins.Count) return;

        var (pin, image) = _pins[index];
        double scale = WindowHelpers.GetScale(_hwnd);
        double width = pin.Item.Bounds.Width / scale, height = pin.Item.Bounds.Height / scale;
        double stageWidth = _stage.ActualWidth, stageHeight = _stage.ActualHeight;
        double fit = Math.Min(1, Math.Min((stageWidth - 24) / width, (stageHeight - 24) / height));
        width *= fit;
        height *= fit;

        var shown = element.TransformToVisual(_stage).TransformBounds(new Windows.Foundation.Rect(0, 0, element.ActualWidth, element.ActualHeight));
        if (width <= shown.Width * 1.1) return; // it's already about full size

        _enlargedImage.Source = image;
        _enlarged.Width = width;
        _enlarged.Height = height;
        Canvas.SetLeft(_enlarged, Math.Clamp(shown.X + shown.Width / 2 - width / 2, 12, Math.Max(12, stageWidth - width - 12)));
        Canvas.SetTop(_enlarged, Math.Clamp(shown.Y + shown.Height / 2 - height / 2, 12, Math.Max(12, stageHeight - height - 12)));
        _dim.Width = stageWidth;
        _dim.Height = stageHeight;
        _dim.Visibility = _enlarged.Visibility = Visibility.Visible;
    }

    private void HideEnlarged()
    {
        _dim.Visibility = _enlarged.Visibility = Visibility.Collapsed;
        _enlargedImage.Source = null;
    }

    // ---- One pin: copy or save ---------------------------------------------------------------

    private MenuFlyout PinMenu(int index)
    {
        var (pin, _) = _pins[index];
        var menu = new MenuFlyout();
        menu.Opening += (_, _) =>
        {
            _hoverTimer.Stop();
            HideEnlarged();
        };

        if (pin.Item is CollageNote note)
        {
            menu.Items.Add(MenuItem("Copy text", "", () =>
            {
                SnipStorage.CopyTextToClipboard(note.Text);
                ShowStatus("Note text copied");
                return Task.CompletedTask;
            }));
        }

        menu.Items.Add(MenuItem("Copy image", "", () =>
        {
            SnipStorage.CopyPngToClipboard(pin.Png);
            ShowStatus("Image copied");
            return Task.CompletedTask;
        }));
        menu.Items.Add(MenuItem("Save image as…", "", async () =>
        {
            string suggested = pin.Item is CollageNote n ? n.Title : Path.GetFileNameWithoutExtension(SnipStorage.CreateFileName());
            if (await BoardExport.PickPngAsync(_hwnd, suggested) is not { } path) return;
            await File.WriteAllBytesAsync(path, pin.Png);
            ShowStatus($"Saved as {Path.GetFileName(path)}");
        }));
        return menu;
    }

    private MenuFlyoutItem MenuItem(string text, string glyph, Func<Task> action)
    {
        var item = new MenuFlyoutItem { Text = text, Icon = new FontIcon { Glyph = glyph } };
        item.Click += async (_, _) =>
        {
            try
            {
                await action();
            }
            catch (Exception ex)
            {
                Log.Error($"Preview action '{text}' failed", ex);
                ShowStatus("That didn't work. The log has details.");
            }
        };
        return item;
    }

    private async Task ExportAsync()
    {
        if (Content.XamlRoot is null) return;
        if (await BoardExport.RunAsync(Content.XamlRoot, _hwnd, _boardId) is { } outcome) ShowStatus($"{outcome.Title}. {outcome.Message}");
    }

    private void ShowStatus(string text)
    {
        _status.Text = text;
        _statusTimer.Stop();
        _statusTimer.Start();
    }
}
