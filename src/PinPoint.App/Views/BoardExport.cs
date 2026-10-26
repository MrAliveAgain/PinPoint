using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using PinPoint.Core.Boards;
using PinPoint.Core.Diagnostics;
using PinPoint.Services;
using Windows.Storage.Pickers;

namespace PinPoint.Views;

/// <summary>How an export went, for the caller to show (null result = cancelled).</summary>
public sealed record ExportOutcome(InfoBarSeverity Severity, string Title, string Message, string? SavedPath);

/// <summary>
/// "Export as image": pick which pins go in (click to tick), pack them together or keep the on-screen layout,
/// optionally add snapshots of pinned windows, and see the exact picture before saving or copying it.
/// Shared by the Boards page and the board preview, so it takes the window to show in.
/// </summary>
public static class BoardExport
{
    /// <summary>The picture preview is drawn at most this big (pixels; shown at half that).</summary>
    private const int PreviewPixels = 720;

    /// <param name="images">The pins' pictures if the caller already loaded them (the board preview has).</param>
    /// <param name="selected">Which pins start ticked; null = all (window snapshots per the last choice).</param>
    public static async Task<ExportOutcome?> RunAsync(XamlRoot xamlRoot, nint ownerWindow, BoardPreview preview,
        IReadOnlyList<(PreviewPin Pin, ImageSource Image)>? images = null, IEnumerable<int>? selected = null)
    {
        var settings = AppHost.Current.Settings;
        images ??= await PinPicker.LoadImagesAsync(preview);
        if (images.Count == 0) return new(InfoBarSeverity.Warning, "Nothing to export", "There are no snips, notes or windows to put in an image.", null);

        var windowPins = Enumerable.Range(0, images.Count).Where(i => images[i].Pin.IsWindow).ToList();
        selected ??= Enumerable.Range(0, images.Count).Where(i => !images[i].Pin.IsWindow || settings.Current.ExportIncludesPinnedWindows);

        var picker = new PinPicker(ownerWindow, images, settings.Current.ExportPacked, selected, maxWidth: 520, maxHeight: 380);
        var compact = new CheckBox { Content = "Compact (no empty space between pins)", IsChecked = picker.Compact };
        var includeWindows = new CheckBox
        {
            Content = "Include pinned windows",
            IsChecked = windowPins.Any(i => picker.Selected.Contains(i)),
            Visibility = windowPins.Count > 0 ? Visibility.Visible : Visibility.Collapsed,
        };

        // Background: a style, plus a colour for "Solid colour".
        string colorHex = settings.Current.ExportBackgroundColor;
        var choices = CollageBackground.Choices;
        var backgroundBox = new ComboBox
        {
            ItemsSource = choices.Select(c => c.Name).ToList(),
            SelectedIndex = Math.Max(0, choices.ToList().FindIndex(c => c.Style == settings.Current.ExportBackground)),
            MinWidth = 160,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var swatch = new Border { Width = 18, Height = 18, CornerRadius = new CornerRadius(4), BorderThickness = new Thickness(1), BorderBrush = new SolidColorBrush(Microsoft.UI.Colors.Gray) };
        var hexText = new TextBlock { VerticalAlignment = VerticalAlignment.Center };
        var colorPicker = new ColorPicker
        {
            IsAlphaEnabled = false,
            IsMoreButtonVisible = false,
            IsColorPreviewVisible = false,
            IsColorChannelTextInputVisible = false,
            IsHexInputVisible = true,
        };
        var colorContent = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        colorContent.Children.Add(swatch);
        colorContent.Children.Add(hexText);
        var colorButton = new DropDownButton { Content = colorContent, Flyout = new Flyout { Content = colorPicker }, VerticalAlignment = VerticalAlignment.Center };
        void ShowColor(string hex)
        {
            if (!Core.Settings.HexColor.TryParse(hex, out byte r, out byte g, out byte b)) return;
            colorHex = hex;
            swatch.Background = new SolidColorBrush(Windows.UI.Color.FromArgb(255, r, g, b));
            hexText.Text = hex;
        }
        ShowColor(colorHex);
        if (Core.Settings.HexColor.TryParse(colorHex, out byte r0, out byte g0, out byte b0)) colorPicker.Color = Windows.UI.Color.FromArgb(255, r0, g0, b0);
        // "Image": any picture, filling the background or repeated as a tile.
        string? imagePath = settings.Current.ExportBackgroundImage;
        var imageButton = new Button { VerticalAlignment = VerticalAlignment.Center, MaxWidth = 220 };
        var tileBox = new CheckBox { Content = "Tile", IsChecked = settings.Current.ExportBackgroundTile, MinWidth = 0, VerticalAlignment = VerticalAlignment.Center };
        ToolTipService.SetToolTip(tileBox, "Repeat the picture at its own size, like a texture, instead of stretching it to fill");
        void ShowImage() => imageButton.Content = new TextBlock
        {
            Text = imagePath is null ? "Choose image…" : Path.GetFileName(imagePath),
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        ShowImage();

        CollageBackground Background() =>
            new(choices[Math.Max(0, backgroundBox.SelectedIndex)].Style, colorHex, imagePath, tileBox.IsChecked == true);
        void ShowBackgroundOptions()
        {
            var style = Background().Style;
            colorButton.Visibility = style == BackgroundStyle.Solid ? Visibility.Visible : Visibility.Collapsed;
            imageButton.Visibility = tileBox.Visibility = style == BackgroundStyle.Image ? Visibility.Visible : Visibility.Collapsed;
        }
        ShowBackgroundOptions();

        var backgroundRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        backgroundRow.Children.Add(new TextBlock { Text = "Background", VerticalAlignment = VerticalAlignment.Center, MinWidth = 76 });
        backgroundRow.Children.Add(backgroundBox);
        var optionsRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(84, 0, 0, 0) };
        optionsRow.Children.Add(colorButton);
        optionsRow.Children.Add(imageButton);
        optionsRow.Children.Add(tileBox);
        var backgroundPanel = new StackPanel { Spacing = 6 };
        backgroundPanel.Children.Add(backgroundRow);
        backgroundPanel.Children.Add(optionsRow);

        var result = new Image { Stretch = Stretch.Uniform, MaxWidth = PreviewPixels / 2.0, MaxHeight = PreviewPixels / 2.0 };
        var rendering = new ProgressRing { IsActive = true, Width = 28, Height = 28 };
        var size = Caption(string.Empty);

        var dialog = new ContentDialog
        {
            XamlRoot = xamlRoot,
            Title = $"Export “{preview.Name}” as an image",
            Content = Layout(picker, result, rendering, size, backgroundPanel, compact, includeWindows, windowPins.Count > 0),
            PrimaryButtonText = "Save as…",
            SecondaryButtonText = "Copy",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
        };
        dialog.Resources["ContentDialogMaxWidth"] = 1100.0;

        // Redraw the picture a moment after the last change (ticking several pins quickly draws once).
        int version = 0;
        var redraw = DispatcherQueue.GetForCurrentThread().CreateTimer();
        redraw.Interval = TimeSpan.FromMilliseconds(150);
        redraw.IsRepeating = false;
        redraw.Tick += async (_, _) =>
        {
            int mine = ++version;
            var items = picker.Selected.Select(i => images[i].Pin.Item).ToList();
            dialog.IsPrimaryButtonEnabled = dialog.IsSecondaryButtonEnabled = items.Count > 0;
            rendering.Visibility = Visibility.Visible;
            try
            {
                var png = items.Count == 0 ? null : await BoardService.RenderAsync(items, picker.Compact, Background(), PreviewPixels, PreviewPixels);
                if (mine != version) return; // a newer change is already drawing
                result.Source = png is null ? null : await ToImageAsync(png);
                size.Text = items.Count == 0
                    ? "Tick at least one pin."
                    : BoardService.ExportSize(items, picker.Compact) is { } full
                        ? $"{full.Width:N0} × {full.Height:N0} px · {items.Count} of {images.Count} pins"
                        : string.Empty;
            }
            catch (Exception ex)
            {
                Log.Error("Export preview failed", ex);
            }
            finally
            {
                if (mine == version) rendering.Visibility = Visibility.Collapsed;
            }
        };
        void Redraw()
        {
            redraw.Stop();
            redraw.Start();
        }

        picker.SelectionChanged += (_, _) =>
        {
            includeWindows.IsChecked = windowPins.Any(i => picker.Selected.Contains(i));
            Redraw();
        };
        compact.Click += (_, _) =>
        {
            picker.Compact = compact.IsChecked == true;
            Redraw();
        };
        async Task ChooseImageAsync()
        {
            if (await PickImageAsync(ownerWindow) is not { } path) return;
            imagePath = path;
            ShowImage();
            Redraw();
        }

        backgroundBox.SelectionChanged += async (_, _) =>
        {
            ShowBackgroundOptions();
            Redraw();
            if (Background().Style == BackgroundStyle.Image && imagePath is null) await ChooseImageAsync();
        };
        imageButton.Click += async (_, _) => await ChooseImageAsync();
        tileBox.Click += (_, _) => Redraw();
        colorPicker.ColorChanged += (_, e) =>
        {
            ShowColor(Core.Settings.HexColor.Format(e.NewColor.R, e.NewColor.G, e.NewColor.B));
            Redraw();
        };
        includeWindows.Click += (_, _) =>
        {
            bool on = includeWindows.IsChecked == true;
            picker.SetSelected(on ? picker.Selected.Union(windowPins) : picker.Selected.Except(windowPins));
        };
        Redraw();

        ContentDialogResult choice;
        try
        {
            choice = await dialog.ShowAsync();
        }
        finally
        {
            redraw.Stop();
            picker.Close();
        }

        if (choice == ContentDialogResult.None) return null;

        var chosen = picker.Selected.Select(i => images[i].Pin.Item).ToList();
        bool pack = picker.Compact;
        var background = Background();
        settings.Update(s =>
        {
            s.ExportPacked = pack;
            s.ExportBackground = background.Style;
            s.ExportBackgroundColor = background.Color;
            s.ExportBackgroundImage = background.ImagePath;
            s.ExportBackgroundTile = background.Tile;
            if (windowPins.Count > 0) s.ExportIncludesPinnedWindows = includeWindows.IsChecked == true;
        });

        string? path = null;
        if (choice == ContentDialogResult.Primary)
        {
            path = await PickPngAsync(ownerWindow, preview.Name);
            if (path is null) return null;
        }

        try
        {
            if (await BoardService.RenderAsync(chosen, pack, background) is not { } png)
                return new(InfoBarSeverity.Warning, "Nothing to export", "None of the chosen pins could be drawn.", null);

            if (path is null)
            {
                await SnipStorage.CopyPngToClipboardAsync(png);
                return new(InfoBarSeverity.Success, "Copied", "The board image is on your clipboard.", null);
            }

            await File.WriteAllBytesAsync(path, png);
            return new(InfoBarSeverity.Success, "Exported", $"Saved as {Path.GetFileName(path)}.", path);
        }
        catch (Exception ex)
        {
            Log.Error("Board export failed", ex);
            return new(InfoBarSeverity.Error, "Couldn't export the image", "The log in %LocalAppData%\\PinPoint\\logs has the details.", null);
        }
    }

    /// <summary>Asks where to save a PNG (starting in Pictures). Null if cancelled.</summary>
    public static async Task<string?> PickPngAsync(nint ownerWindow, string suggestedName)
    {
        var picker = new FileSavePicker
        {
            SuggestedStartLocation = PickerLocationId.PicturesLibrary,
            SuggestedFileName = SafeFileName(suggestedName),
        };
        picker.FileTypeChoices.Add("PNG image", [".png"]);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, ownerWindow);

        return (await picker.PickSaveFileAsync())?.Path;
    }

    /// <summary>Asks for a background picture (starting in Pictures). Null if cancelled.</summary>
    private static async Task<string?> PickImageAsync(nint ownerWindow)
    {
        var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.PicturesLibrary, ViewMode = PickerViewMode.Thumbnail };
        foreach (string type in (string[])[".png", ".jpg", ".jpeg", ".webp", ".bmp", ".gif"]) picker.FileTypeFilter.Add(type);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, ownerWindow);
        return (await picker.PickSingleFileAsync())?.Path;
    }

    /// <summary>Pins to choose from on the left; the picture as it will be saved, and the options, on the right.</summary>
    private static Grid Layout(PinPicker picker, Image result, ProgressRing rendering, TextBlock size,
        FrameworkElement backgroundRow, CheckBox compact, CheckBox includeWindows, bool hasWindows)
    {
        var all = new HyperlinkButton { Content = "All", Padding = new Thickness(4, 0, 4, 0) };
        all.Click += (_, _) => picker.SetSelected(Enumerable.Range(0, picker.Pins.Count));
        var none = new HyperlinkButton { Content = "None", Padding = new Thickness(4, 0, 4, 0) };
        none.Click += (_, _) => picker.SetSelected([]);

        var pinsHeader = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        pinsHeader.Children.Add(new TextBlock { Text = "Click pins to choose what goes in", VerticalAlignment = VerticalAlignment.Center });
        pinsHeader.Children.Add(all);
        pinsHeader.Children.Add(none);

        var left = new StackPanel { Spacing = 8 };
        left.Children.Add(pinsHeader);
        left.Children.Add(picker.View);
        left.Children.Add(Caption("Hover a pin to see it full size · right-click to copy or save just that one"));

        var pictureBox = new Grid
        {
            MinWidth = 220,
            MinHeight = 140,
            Padding = new Thickness(8),
            CornerRadius = new CornerRadius(6),
            Background = (Brush)Application.Current.Resources["CardBackgroundFillColorSecondaryBrush"],
        };
        rendering.HorizontalAlignment = HorizontalAlignment.Center;
        rendering.VerticalAlignment = VerticalAlignment.Center;
        pictureBox.Children.Add(result);
        pictureBox.Children.Add(rendering);

        var right = new StackPanel { Spacing = 8, MaxWidth = PreviewPixels / 2.0 + 16 };
        right.Children.Add(new TextBlock { Text = "The image" });
        right.Children.Add(pictureBox);
        right.Children.Add(size);
        right.Children.Add(backgroundRow);
        right.Children.Add(compact);
        right.Children.Add(includeWindows);
        if (hasWindows) right.Children.Add(Caption("Pinned windows are a snapshot of how they look right now, only in the image. They never become part of a board."));

        var grid = new Grid { ColumnSpacing = 24 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(right, 1);
        grid.Children.Add(left);
        grid.Children.Add(right);
        return grid;
    }

    private static async Task<ImageSource> ToImageAsync(byte[] png)
    {
        var bitmap = new BitmapImage();
        using (var stream = new MemoryStream(png)) await bitmap.SetSourceAsync(stream.AsRandomAccessStream());
        return bitmap;
    }

    private static string SafeFileName(string name)
    {
        string safe = string.Concat(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? ' ' : c)).Trim();
        return safe.Length == 0 ? "Board" : safe;
    }

    private static TextBlock Caption(string text) => new()
    {
        Text = text,
        TextWrapping = TextWrapping.Wrap,
        Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
        Opacity = 0.75,
    };
}
