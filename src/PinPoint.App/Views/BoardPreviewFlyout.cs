using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using PinPoint.Core.Diagnostics;
using PinPoint.Core.Interop;
using PinPoint.Core.Windows;
using PinPoint.Services;

namespace PinPoint.Views;

/// <summary>
/// A quick look at a saved board, popping out of its thumbnail (click away or Esc to close). The pins are a
/// <see cref="PinPicker"/>: hover to see one full size, click to choose which go into an export, right-click to
/// copy or save one. Open and Export act on the board; Export starts with the pins ticked here.
/// </summary>
public sealed class BoardPreviewFlyout
{
    private readonly AppHost _host = AppHost.Current;
    private readonly string _boardId;
    private readonly nint _ownerWindow;
    private readonly Func<Task> _open;
    private readonly Func<BoardPreview, IReadOnlyList<(PreviewPin, ImageSource)>, IReadOnlyList<int>, Task> _export;
    private readonly Action<string> _report;
    private readonly Flyout _flyout;
    private readonly Grid _stage = new() { HorizontalAlignment = HorizontalAlignment.Center };
    private readonly ToggleSwitch _compact;
    private readonly Button _exportButton = new() { Content = "Export image…", IsEnabled = false };
    private readonly Button _copyButton = new() { Content = "Copy", IsEnabled = false };
    private readonly double _maxWidth, _maxHeight;
    private BoardPreview? _preview;
    private PinPicker? _picker;

    private BoardPreviewFlyout(string boardId, string name, nint ownerWindow, Func<Task> open,
        Func<BoardPreview, IReadOnlyList<(PreviewPin, ImageSource)>, IReadOnlyList<int>, Task> export, Action<string> report)
    {
        _boardId = boardId;
        _ownerWindow = ownerWindow;
        _open = open;
        _export = export;
        _report = report;

        Win32.GetCursorPos(out var cursor);
        var (_, work) = WindowEnumerator.GetMonitorAt(cursor.X, cursor.Y);
        double dpi = Win32.GetDpiForWindow(ownerWindow) / 96.0;
        _maxWidth = work.Width / dpi * 0.85;
        _maxHeight = work.Height / dpi * 0.75;

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
            if (_picker is not null) _picker.Compact = _compact.IsOn;
        };

        _stage.Children.Add(new ProgressRing { IsActive = true, Width = 32, Height = 32, Margin = new Thickness(40) });

        var presenter = new Style(typeof(FlyoutPresenter));
        presenter.Setters.Add(new Setter(FrameworkElement.MaxWidthProperty, double.PositiveInfinity));
        presenter.Setters.Add(new Setter(FrameworkElement.MaxHeightProperty, double.PositiveInfinity));
        presenter.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(12)));
        presenter.Setters.Add(new Setter(Control.CornerRadiusProperty, new CornerRadius(8)));
        presenter.Setters.Add(new Setter(Control.BackgroundProperty, new SolidColorBrush(Microsoft.UI.Colors.Transparent))); // let the acrylic through

        _flyout = new Flyout
        {
            Content = BuildContent(name),
            FlyoutPresenterStyle = presenter,
            Placement = FlyoutPlacementMode.RightEdgeAlignedTop,
            ShouldConstrainToRootBounds = false,
            SystemBackdrop = new DesktopAcrylicBackdrop(), // frosted, see-through background like Windows' own flyouts
        };
        _flyout.Closed += (_, _) => _picker?.Close();
    }

    /// <summary>Pops the preview out of <paramref name="anchor"/> (the board's thumbnail).</summary>
    public static void Show(FrameworkElement anchor, nint ownerWindow, string boardId, string name, Func<Task> open,
        Func<BoardPreview, IReadOnlyList<(PreviewPin, ImageSource)>, IReadOnlyList<int>, Task> export, Action<string> report)
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

        _exportButton.Click += async (_, _) =>
        {
            if (_preview is null || _picker is null) return;
            var (preview, pins, selected) = (_preview, _picker.Pins, _picker.Selected);
            _flyout.Hide();
            await _export(preview, pins, selected);
        };

        _copyButton.Click += async (_, _) => await CopyAsync();
        ToolTipService.SetToolTip(_copyButton, "Copy the ticked pins as one image, ready to paste");

        var header = new Grid { ColumnSpacing = 12 };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        for (int i = 0; i < 4; i++) header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var title = new StackPanel { VerticalAlignment = VerticalAlignment.Center, MinWidth = 160 };
        title.Children.Add(new TextBlock
        {
            Text = name,
            Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"],
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        title.Children.Add(new TextBlock
        {
            Text = "Hover to see a pin full size · click to choose what to export · right-click to copy or save one",
            Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
            Opacity = 0.75,
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 420,
        });
        header.Children.Add(title);
        Place(header, _compact, 1);
        Place(header, _copyButton, 2);
        Place(header, _exportButton, 3);
        Place(header, open, 4);

        var root = new StackPanel { Spacing = 12 };
        root.Children.Add(header);
        root.Children.Add(_stage);
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
            _preview = await _host.Boards.GetPreviewAsync(_boardId);
            var pins = _preview is null ? [] : await PinPicker.LoadImagesAsync(_preview);

            _stage.Children.Clear();
            if (pins.Count == 0)
            {
                _stage.Children.Add(new TextBlock { Text = "This board is empty.", Margin = new Thickness(24) });
                return;
            }

            _picker = new PinPicker(_ownerWindow, pins, _compact.IsOn, selected: null, _maxWidth, _maxHeight);
            _picker.Reported += _report;
            _picker.SelectionChanged += (_, _) => UpdateExportButton();
            _stage.Children.Add(_picker.View);
            UpdateExportButton();
        }
        catch (Exception ex)
        {
            Log.Error($"Could not load preview of board {_boardId}", ex);
            _stage.Children.Clear();
            _stage.Children.Add(new TextBlock { Text = "Couldn't load this board.", Margin = new Thickness(24) });
        }
    }

    /// <summary>"Export image…" with everything ticked, "Export 2 of 5…" for a subset.</summary>
    private void UpdateExportButton()
    {
        if (_picker is null) return;
        int ticked = _picker.Selected.Count, total = _picker.Pins.Count;
        _exportButton.IsEnabled = _copyButton.IsEnabled = ticked > 0;
        _exportButton.Content = ticked == total || ticked == 0 ? "Export image…" : $"Export {ticked} of {total}…";
        _copyButton.Content = ticked == total || ticked == 0 ? "Copy" : $"Copy {ticked}";
    }

    /// <summary>The ticked pins as one image (compact or not, as shown here) straight onto the clipboard.</summary>
    private async Task CopyAsync()
    {
        if (_picker is null) return;
        try
        {
            var items = _picker.Selected.Select(i => _picker.Pins[i].Pin.Item).ToList();
            if (await BoardService.RenderAsync(items, _picker.Compact) is not { } png) return;
            await SnipStorage.CopyPngToClipboardAsync(png);
            _copyButton.Content = "Copied ✓";
            _report(items.Count == _picker.Pins.Count ? "The board image is on your clipboard." : $"{items.Count} pins are on your clipboard as one image.");
        }
        catch (Exception ex)
        {
            Log.Error("Could not copy from the board preview", ex);
        }
    }
}
