using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
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
/// A board's pins at half size, compact (packed together) or where they were on screen, used by the board
/// preview and the export dialog. Click a pin to tick or untick it (which pins get exported); hover it to
/// see it full size in a <see cref="PinPeekWindow"/>; right-click to copy or save just that pin.
/// </summary>
public sealed class PinPicker
{
    private static readonly TimeSpan HoverDelay = TimeSpan.FromMilliseconds(200);

    /// <summary>Space around the pins, in screen pixels.</summary>
    private const int StageMargin = 24;

    /// <summary>Pins show at half their real size; hovering one shows it full size.</summary>
    private const double PreviewScale = 0.5;

    private readonly nint _ownerWindow;
    private readonly IReadOnlyList<(PreviewPin Pin, ImageSource Image)> _pins;
    private readonly HashSet<int> _selected;
    private readonly Canvas _canvas = new();
    private readonly Viewbox _viewbox;
    private readonly Brush _edge;
    private readonly Brush _accent;
    private readonly DispatcherQueueTimer _hoverTimer;
    private PinPeekWindow? _peek;
    private bool _compact;

    private Grid? _hovered;
    private Windows.Foundation.Point _hoverLocal;
    private Win32.POINT _hoverScreen;

    public PinPicker(nint ownerWindow, IReadOnlyList<(PreviewPin Pin, ImageSource Image)> pins, bool compact,
        IEnumerable<int>? selected, double maxWidth, double maxHeight)
    {
        _ownerWindow = ownerWindow;
        _pins = pins;
        _compact = compact;
        _selected = selected is null ? [.. Enumerable.Range(0, pins.Count)] : [.. selected];

        bool dark = Application.Current.RequestedTheme == ApplicationTheme.Dark;
        _edge = new SolidColorBrush(dark ? Color.FromArgb(51, 255, 255, 255) : Color.FromArgb(31, 0, 0, 0));
        _accent = new SolidColorBrush(AccentColor);

        _hoverTimer = DispatcherQueue.GetForCurrentThread().CreateTimer();
        _hoverTimer.Interval = HoverDelay;
        _hoverTimer.IsRepeating = false;
        _hoverTimer.Tick += (_, _) => ShowPeek();

        // Smaller boards show at exactly half size; bigger ones shrink to fit.
        _viewbox = new Viewbox
        {
            Stretch = Stretch.Uniform,
            StretchDirection = StretchDirection.DownOnly,
            Child = _canvas,
            MaxWidth = maxWidth,
            MaxHeight = maxHeight,
        };
        LayOut();
    }

    /// <summary>Raised when a pin is ticked or unticked.</summary>
    public event EventHandler? SelectionChanged;

    /// <summary>A short message after copying or saving one pin (for the host to show).</summary>
    public event Action<string>? Reported;

    public FrameworkElement View => _viewbox;

    public IReadOnlyList<(PreviewPin Pin, ImageSource Image)> Pins => _pins;

    /// <summary>Indexes of the ticked pins, in board order.</summary>
    public IReadOnlyList<int> Selected => [.. _selected.Order()];

    public bool Compact
    {
        get => _compact;
        set
        {
            if (_compact == value) return;
            _compact = value;
            LayOut();
        }
    }

    public static Color AccentColor =>
        Application.Current.Resources.TryGetValue("SystemAccentColor", out var accent) && accent is Color c ? c : Color.FromArgb(255, 76, 194, 255);

    /// <summary>Loads each pin's picture for display.</summary>
    public static async Task<List<(PreviewPin Pin, ImageSource Image)>> LoadImagesAsync(BoardPreview preview)
    {
        var pins = new List<(PreviewPin, ImageSource)>();
        foreach (var pin in preview.Pins)
        {
            var bitmap = new BitmapImage();
            using (var stream = new MemoryStream(pin.Png)) await bitmap.SetSourceAsync(stream.AsRandomAccessStream());
            pins.Add((pin, bitmap));
        }

        return pins;
    }

    public void SetSelected(IEnumerable<int> indexes)
    {
        _selected.Clear();
        _selected.UnionWith(indexes.Where(i => i >= 0 && i < _pins.Count));
        RefreshTicks();
        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Hides the full-size peek for good (call when the host closes).</summary>
    public void Close()
    {
        _hoverTimer.Stop();
        _peek?.Close();
        _peek = null;
    }

    // ---- Layout ------------------------------------------------------------------------------

    /// <summary>Places every pin at half size (in DIPs); the Viewbox shrinks the lot if it's too big.</summary>
    private void LayOut()
    {
        HidePeek();
        _canvas.Children.Clear();

        var items = BoardService.Arrange([.. _pins.Select(p => p.Pin.Item)], _compact);
        if (CollageLayout.Fit(items.Select(i => i.Bounds), StageMargin, int.MaxValue, int.MaxValue) is not { } layout) return;

        double pxPerDip = Win32.GetDpiForWindow(_ownerWindow) / 96.0 / PreviewScale;
        _canvas.Width = layout.Width / pxPerDip;
        _canvas.Height = layout.Height / pxPerDip;

        for (int i = 0; i < items.Count; i++)
        {
            var (x, y, width, height) = layout.Map(items[i].Bounds);
            var element = PinElement(i, width / pxPerDip, height / pxPerDip);
            Canvas.SetLeft(element, x / pxPerDip);
            Canvas.SetTop(element, y / pxPerDip);
            _canvas.Children.Add(element);
        }

        RefreshTicks();
    }

    /// <summary>The pin's picture with an edge (accent while hovered) and a tick badge in the corner.</summary>
    private Grid PinElement(int index, double width, double height)
    {
        var picture = new Border
        {
            CornerRadius = new CornerRadius(6),
            BorderThickness = new Thickness(2),
            BorderBrush = _edge,
            Child = new Image { Source = _pins[index].Image, Stretch = Stretch.Fill },
        };

        var tick = new Border
        {
            Width = 20,
            Height = 20,
            CornerRadius = new CornerRadius(10),
            Background = _accent,
            BorderThickness = new Thickness(1.5),
            BorderBrush = new SolidColorBrush(Color.FromArgb(255, 255, 255, 255)),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 4, 4, 0),
            Child = new FontIcon { Glyph = "", FontSize = 11, Foreground = new SolidColorBrush(Color.FromArgb(255, 255, 255, 255)) },
            IsHitTestVisible = false,
        };

        var element = new Grid
        {
            Width = width,
            Height = height,
            Tag = index,
            Background = new SolidColorBrush(Color.FromArgb(0, 0, 0, 0)), // hit-testable everywhere
            ContextFlyout = PinMenu(index),
        };
        ToolTipService.SetToolTip(element, _pins[index].Pin.IsWindow ? "Pinned window (snapshot)" : null);
        element.Children.Add(picture);
        element.Children.Add(tick);
        element.Tapped += Pin_Tapped;
        element.PointerEntered += Pin_PointerEntered;
        element.PointerMoved += Pin_PointerMoved;
        element.PointerExited += Pin_PointerExited;
        return element;
    }

    private void RefreshTicks()
    {
        foreach (var element in _canvas.Children.OfType<Grid>())
        {
            if (element.Tag is not int index) continue;
            bool on = _selected.Contains(index);
            element.Children[0].Opacity = on ? 1 : 0.35;
            element.Children[1].Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    private void Pin_Tapped(object sender, TappedRoutedEventArgs e)
    {
        if (sender is not Grid { Tag: int index }) return;
        if (!_selected.Remove(index)) _selected.Add(index);
        RefreshTicks();
        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    // ---- Hover: outline at once, full size a moment later ------------------------------------

    private void Pin_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        if (_hovered is not null) Edge(_hovered, _edge);
        _hovered = (Grid)sender;
        Edge(_hovered, _accent);
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
        Edge(_hovered, _edge);
        _hovered = null;
        HidePeek();
    }

    private static void Edge(Grid element, Brush brush) => ((Border)element.Children[0]).BorderBrush = brush;

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

        // Element DIPs → screen pixels, including the Viewbox's shrink when the board is too big for half size.
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
                Reported?.Invoke("The note's text is on your clipboard.");
                return Task.CompletedTask;
            }));
        }

        menu.Items.Add(MenuItem("Copy image", "", async () =>
        {
            await SnipStorage.CopyPngToClipboardAsync(pin.Png);
            Reported?.Invoke("The image is on your clipboard.");
        }));
        menu.Items.Add(MenuItem("Save image as…", "", async () =>
        {
            string suggested = pin.Item is CollageNote n ? n.Title : Path.GetFileNameWithoutExtension(SnipStorage.CreateFileName());
            if (await BoardExport.PickPngAsync(_ownerWindow, suggested) is not { } path) return;
            await File.WriteAllBytesAsync(path, pin.Png);
            Reported?.Invoke($"Saved as {Path.GetFileName(path)}.");
        }));
        return menu;
    }

    private static MenuFlyoutItem MenuItem(string text, string glyph, Func<Task> action)
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
                Log.Error($"Pin action '{text}' failed", ex);
            }
        };
        return item;
    }
}
