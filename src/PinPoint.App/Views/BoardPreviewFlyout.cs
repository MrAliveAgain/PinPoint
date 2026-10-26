using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using PinPoint.Core.Boards;
using PinPoint.Core.Diagnostics;
using PinPoint.Core.Geometry;
using PinPoint.Core.Interop;
using PinPoint.Core.Windows;
using PinPoint.Services;
using Windows.UI;

namespace PinPoint.Views;

/// <summary>
/// A quick look at a saved board, popping out of its thumbnail (click away or Esc to close). Pins show at
/// half size, compact (packed together) or where they were on screen. Hovering one outlines it and then shows
/// it full size in a <see cref="PinPeekWindow"/>; right-click copies or saves just that pin. Open and Export
/// act on the whole board.
/// </summary>
public sealed class BoardPreviewFlyout
{
    private static readonly TimeSpan HoverDelay = TimeSpan.FromMilliseconds(200);

    /// <summary>Space around the pins, in screen pixels.</summary>
    private const int StageMargin = 24;

    /// <summary>Pins show at half their real size; hovering one shows it full size.</summary>
    private const double PreviewScale = 0.5;

    private readonly AppHost _host = AppHost.Current;
    private readonly string _boardId;
    private readonly nint _ownerWindow;
    private readonly Func<Task> _open;
    private readonly Func<Task> _export;
    private readonly Action<string> _report;
    private readonly bool _dark = Application.Current.RequestedTheme == ApplicationTheme.Dark;
    private readonly Brush _edge;
    private readonly Brush _hoverEdge;

    private readonly Flyout _flyout;
    private readonly Canvas _canvas = new();
    private readonly Viewbox _viewbox;
    private readonly ProgressRing _loading = new() { IsActive = true, Width = 32, Height = 32, Margin = new Thickness(40) };
    private readonly TextBlock _empty = new() { Text = "This board is empty.", Margin = new Thickness(24), Visibility = Visibility.Collapsed };
    private readonly ToggleSwitch _compact;
    private readonly DispatcherQueueTimer _hoverTimer;
    private PinPeekWindow? _peek;

    private List<(PreviewPin Pin, BitmapImage Image)> _pins = [];
    private Border? _hovered;
    private Windows.Foundation.Point _hoverLocal;
    private Win32.POINT _hoverScreen;

    private BoardPreviewFlyout(string boardId, string name, nint ownerWindow, Func<Task> open, Func<Task> export, Action<string> report)
    {
        _boardId = boardId;
        _ownerWindow = ownerWindow;
        _open = open;
        _export = export;
        _report = report;
        _edge = new SolidColorBrush(_dark ? Color.FromArgb(51, 255, 255, 255) : Color.FromArgb(31, 0, 0, 0));
        _hoverEdge = new SolidColorBrush(Application.Current.Resources.TryGetValue("SystemAccentColor", out var accent) && accent is Color c
            ? c
            : Color.FromArgb(255, 76, 194, 255));

        Win32.GetCursorPos(out var cursor);
        var (_, work) = WindowEnumerator.GetMonitorAt(cursor.X, cursor.Y);
        double dpi = Win32.GetDpiForWindow(ownerWindow) / 96.0;

        _compact = new ToggleSwitch
        {
            OnContent = "Compact",
            OffContent = "Compact",
            IsOn = _host.Settings.Current.PreviewPacked,
            MinWidth = 0,
            VerticalAlignment = VerticalAlignment.Center,
        };
        _compact.Toggled += (_, _) =>
        {
            _host.Settings.Update(s => s.PreviewPacked = _compact.IsOn);
            LayOut();
        };

        _hoverTimer = DispatcherQueue.GetForCurrentThread().CreateTimer();
        _hoverTimer.Interval = HoverDelay;
        _hoverTimer.IsRepeating = false;
        _hoverTimer.Tick += (_, _) => ShowPeek();

        // Never bigger than most of the screen; smaller boards show at exactly half size.
        _viewbox = new Viewbox
        {
            Stretch = Stretch.Uniform,
            StretchDirection = StretchDirection.DownOnly,
            Child = _canvas,
            MaxWidth = work.Width / dpi * 0.85,
            MaxHeight = work.Height / dpi * 0.75,
        };

        var presenter = new Style(typeof(FlyoutPresenter));
        presenter.Setters.Add(new Setter(FrameworkElement.MaxWidthProperty, double.PositiveInfinity));
        presenter.Setters.Add(new Setter(FrameworkElement.MaxHeightProperty, double.PositiveInfinity));
        presenter.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(12)));

        _flyout = new Flyout
        {
            Content = BuildContent(name),
            FlyoutPresenterStyle = presenter,
            Placement = FlyoutPlacementMode.RightEdgeAlignedTop,
            ShouldConstrainToRootBounds = false,
        };
        _flyout.Closed += (_, _) =>
        {
            _hoverTimer.Stop();
            _peek?.Close();
            _peek = null;
        };
    }

    /// <summary>Pops the preview out of <paramref name="anchor"/> (the board's thumbnail).</summary>
    public static void Show(FrameworkElement anchor, nint ownerWindow, string boardId, string name,
        Func<Task> open, Func<Task> export, Action<string> report)
    {
        var preview = new BoardPreviewFlyout(boardId, name, ownerWindow, open, export, report);
        preview._flyout.ShowAt(anchor);
        _ = preview.LoadAsync();
    }

    private StackPanel BuildContent(string name)
    {
        var open = new Button { Content = "Open", Style = (Style)Application.Current.Resources["AccentButtonStyle"] };
        open.Click += async (_, _) =>
        {
            _flyout.Hide();
            await _open();
        };

        var export = new Button { Content = "Export image…" };
        export.Click += async (_, _) =>
        {
            _flyout.Hide();
            await _export();
        };

        var header = new Grid { ColumnSpacing = 12 };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        for (int i = 0; i < 3; i++) header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var title = new StackPanel { VerticalAlignment = VerticalAlignment.Center, MinWidth = 160 };
        title.Children.Add(new TextBlock
        {
            Text = name,
            Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"],
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        title.Children.Add(new TextBlock
        {
            Text = "Hover a pin to see it full size · right-click to copy or save it",
            Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
            Opacity = 0.75,
        });
        header.Children.Add(title);
        Place(header, _compact, 1);
        Place(header, export, 2);
        Place(header, open, 3);

        var stage = new Grid { HorizontalAlignment = HorizontalAlignment.Center };
        stage.Children.Add(_viewbox);
        stage.Children.Add(_loading);
        stage.Children.Add(_empty);

        var root = new StackPanel { Spacing = 12 };
        root.Children.Add(header);
        root.Children.Add(stage);
        return root;

        static void Place(Grid grid, FrameworkElement element, int column)
        {
            Grid.SetColumn(element, column);
            grid.Children.Add(element);
        }
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

    /// <summary>Places every pin at half size (in DIPs); the flyout grows or shrinks to fit.</summary>
    private void LayOut()
    {
        HidePeek();
        _canvas.Children.Clear();

        var items = BoardService.Arrange([.. _pins.Select(p => p.Pin.Item)], _compact.IsOn);
        if (CollageLayout.Fit(items.Select(i => i.Bounds), StageMargin, int.MaxValue, int.MaxValue) is not { } layout)
        {
            _empty.Visibility = _loading.IsActive ? Visibility.Collapsed : Visibility.Visible;
            return;
        }

        double pxPerDip = Win32.GetDpiForWindow(_ownerWindow) / 96.0 / PreviewScale;
        _canvas.Width = layout.Width / pxPerDip;
        _canvas.Height = layout.Height / pxPerDip;

        for (int i = 0; i < items.Count; i++)
        {
            var (x, y, width, height) = layout.Map(items[i].Bounds);
            var element = new Border
            {
                Width = width / pxPerDip,
                Height = height / pxPerDip,
                CornerRadius = new CornerRadius(6),
                BorderThickness = new Thickness(2),
                BorderBrush = _edge,
                Child = new Image { Source = _pins[i].Image, Stretch = Stretch.Fill },
                Tag = i,
                ContextFlyout = PinMenu(i),
            };
            Canvas.SetLeft(element, x / pxPerDip);
            Canvas.SetTop(element, y / pxPerDip);
            element.PointerEntered += Pin_PointerEntered;
            element.PointerMoved += Pin_PointerMoved;
            element.PointerExited += Pin_PointerExited;
            _canvas.Children.Add(element);
        }
    }

    // ---- Hover: outline at once, full size a moment later ------------------------------------

    private void Pin_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        if (_hovered is not null) _hovered.BorderBrush = _edge;
        _hovered = (Border)sender;
        _hovered.BorderBrush = _hoverEdge;
        TrackPointer(e);
        _hoverTimer.Stop();
        _hoverTimer.Start();
    }

    private void Pin_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (ReferenceEquals(_hovered, sender)) TrackPointer(e);
    }

    private void Pin_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (!ReferenceEquals(_hovered, sender)) return;
        _hovered.BorderBrush = _edge;
        _hovered = null;
        HidePeek();
    }

    /// <summary>Where the mouse is, both on screen and within the hovered pin, so the peek can be centred on the pin.</summary>
    private void TrackPointer(PointerRoutedEventArgs e)
    {
        _hoverLocal = e.GetCurrentPoint(_hovered).Position;
        Win32.GetCursorPos(out _hoverScreen);
    }

    /// <summary>The hovered pin at its real on-screen size, centred over it and kept on its monitor.</summary>
    private void ShowPeek()
    {
        if (_hovered is not { Tag: int index } element || index >= _pins.Count) return;

        var (pin, image) = _pins[index];
        double dpi = Win32.GetDpiForWindow(_ownerWindow) / 96.0;

        // Element DIPs → screen pixels, including the Viewbox's shrink (if the board is too big to show at half size).
        double shown = element.TransformToVisual(_viewbox).TransformBounds(new Windows.Foundation.Rect(0, 0, 100, 100)).Width / 100;
        double toScreen = dpi * shown;
        double centerX = _hoverScreen.X + (element.ActualWidth / 2 - _hoverLocal.X) * toScreen;
        double centerY = _hoverScreen.Y + (element.ActualHeight / 2 - _hoverLocal.Y) * toScreen;

        var (_, work) = WindowEnumerator.GetMonitorAt(_hoverScreen.X, _hoverScreen.Y);
        double fit = Math.Min(1, Math.Min(work.Width * 0.95 / pin.Item.Bounds.Width, work.Height * 0.95 / pin.Item.Bounds.Height));
        int width = (int)(pin.Item.Bounds.Width * fit), height = (int)(pin.Item.Bounds.Height * fit);
        if (width <= element.ActualWidth * toScreen * 1.1) return; // it's already about full size

        int x = (int)Math.Clamp(centerX - width / 2.0, work.X, Math.Max(work.X, work.Right - width));
        int y = (int)Math.Clamp(centerY - height / 2.0, work.Y, Math.Max(work.Y, work.Bottom - height));

        try
        {
            _peek ??= new PinPeekWindow();
            _peek.ShowAt(image, new PixelRect(x, y, width, height));
        }
        catch (Exception ex)
        {
            Log.Error("Could not show pin peek", ex);
        }
    }

    private void HidePeek()
    {
        _hoverTimer.Stop();
        _peek?.HidePeek();
    }

    // ---- One pin: copy or save ---------------------------------------------------------------

    private MenuFlyout PinMenu(int index)
    {
        var (pin, _) = _pins[index];
        var menu = new MenuFlyout();
        menu.Opening += (_, _) => HidePeek();

        if (pin.Item is CollageNote note)
        {
            menu.Items.Add(MenuItem("Copy text", "", () =>
            {
                SnipStorage.CopyTextToClipboard(note.Text);
                _report("The note's text is on your clipboard.");
                return Task.CompletedTask;
            }));
        }

        menu.Items.Add(MenuItem("Copy image", "", () =>
        {
            SnipStorage.CopyPngToClipboard(pin.Png);
            _report("The image is on your clipboard.");
            return Task.CompletedTask;
        }));
        menu.Items.Add(MenuItem("Save image as…", "", async () =>
        {
            string suggested = pin.Item is CollageNote n ? n.Title : Path.GetFileNameWithoutExtension(SnipStorage.CreateFileName());
            if (await BoardExport.PickPngAsync(_ownerWindow, suggested) is not { } path) return;
            await File.WriteAllBytesAsync(path, pin.Png);
            _report($"Saved as {Path.GetFileName(path)}.");
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
            }
        };
        return item;
    }
}
