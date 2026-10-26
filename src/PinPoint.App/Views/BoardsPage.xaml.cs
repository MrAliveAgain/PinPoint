using System.Diagnostics;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using PinPoint.Core.Boards;
using PinPoint.Core.Diagnostics;
using PinPoint.Services;
using PinPoint.ViewModels;
using Windows.Storage.Pickers;

namespace PinPoint.Views;

/// <summary>One saved board in the list.</summary>
public sealed class BoardRow(BoardInfo info, bool isCurrent) : ObservableObject
{
    private ImageSource? _thumbnail;

    /// <summary>Set once the preview is drawn (later than the row itself).</summary>
    public ImageSource? Thumbnail
    {
        get => _thumbnail;
        set
        {
            if (SetProperty(ref _thumbnail, value)) OnPropertyChanged(nameof(PlaceholderVisibility));
        }
    }

    public Visibility PlaceholderVisibility => _thumbnail is null ? Visibility.Visible : Visibility.Collapsed;

    public string Id { get; } = info.Id;

    public string Name { get; } = info.Name;

    public string Detail { get; } = $"{Count(info.Snips, "snip")} · {Count(info.Notes, "note")} · edited {Ago(info.Modified)}";

    public bool CanOpen { get; } = !isCurrent;

    public string OpenLabel { get; } = isCurrent ? "Open now" : "Open";

    private static string Count(int n, string noun) => $"{n} {noun}{(n == 1 ? "" : "s")}";

    private static string Ago(DateTime when)
    {
        var span = DateTime.Now - when;
        return span.TotalMinutes < 1 ? "just now"
            : span.TotalHours < 1 ? $"{(int)span.TotalMinutes} min ago"
            : span.TotalDays < 1 ? $"{(int)span.TotalHours} h ago"
            : span.TotalDays < 2 ? "yesterday"
            : when.ToString("d MMM yyyy");
    }
}

public sealed partial class BoardsPage : Page
{
    private readonly AppHost _host = AppHost.Current;

    /// <summary>Loaded previews by board id, with the file time they were read at.</summary>
    private readonly Dictionary<string, (DateTime Stamp, ImageSource Image)> _thumbnails = [];

    private string? _lastExportPath;

    public BoardsPage()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            _host.Boards.Changed += OnChanged;
            _host.Pins.StateChanged += OnChanged;
            Refresh();
        };
        Unloaded += (_, _) =>
        {
            _host.Boards.Changed -= OnChanged;
            _host.Pins.StateChanged -= OnChanged;
        };
    }

    private void OnChanged(object? sender, EventArgs e) => Refresh();

    private void Refresh()
    {
        var boards = _host.Boards;
        if (boards.CurrentName is { } name)
        {
            CurrentTitle.Text = name;
            CurrentDetail.Text = "Open now · saved automatically as you work";
        }
        else
        {
            CurrentTitle.Text = "No board open";
            CurrentDetail.Text = boards.HasPinsToSave
                ? "Your current snips and notes aren't saved as a board yet."
                : "Pin some snips or notes, then save them as a board to come back to later.";
        }

        SaveAsButton.Visibility = boards.IsBoardOpen ? Visibility.Collapsed : Visibility.Visible;
        SaveAsButton.IsEnabled = boards.HasPinsToSave;
        RenameCurrentButton.Visibility = CloseBoardButton.Visibility = boards.IsBoardOpen ? Visibility.Visible : Visibility.Collapsed;
        ExportCurrentButton.Visibility = boards.HasPinsToSave || boards.HasPinnedWindows ? Visibility.Visible : Visibility.Collapsed;

        var rows = boards.List().Select(b => new BoardRow(b, b.Id == boards.CurrentId)).ToList();
        foreach (var row in rows)
        {
            // Show the last preview straight away (no flicker on refresh); swap in a newer one if it was redrawn.
            if (_thumbnails.TryGetValue(row.Id, out var cached)) row.Thumbnail = cached.Image;
            _ = LoadThumbnailAsync(row);
        }

        BoardList.ItemsSource = rows;
        EmptyBoards.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private async Task LoadThumbnailAsync(BoardRow row)
    {
        try
        {
            if (await _host.Boards.GetThumbnailAsync(row.Id) is not { } path) return;

            var stamp = File.GetLastWriteTimeUtc(path);
            if (_thumbnails.TryGetValue(row.Id, out var cached) && cached.Stamp == stamp)
            {
                row.Thumbnail = cached.Image;
                return;
            }

            var bitmap = new BitmapImage();
            using (var file = File.OpenRead(path)) await bitmap.SetSourceAsync(file.AsRandomAccessStream());
            _thumbnails[row.Id] = (stamp, bitmap);
            row.Thumbnail = bitmap;
        }
        catch (Exception ex)
        {
            Log.Error($"Could not show thumbnail for board {row.Id}", ex);
        }
    }

    // ---- Current board -----------------------------------------------------------------------

    private async void SaveAs_Click(object sender, RoutedEventArgs e) => await SaveCurrentAsBoardAsync();

    private async void RenameCurrent_Click(object sender, RoutedEventArgs e)
    {
        if (_host.Boards.CurrentId is { } id && await AskNameAsync("Rename board", _host.Boards.CurrentName) is { } name)
            _host.Boards.Rename(id, name);
    }

    private async void CloseBoard_Click(object sender, RoutedEventArgs e) => await _host.Boards.CloseBoardAsync();

    // ---- Saved boards ------------------------------------------------------------------------

    private async void Open_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: BoardRow row }) return;

        // Like any document app: offer to save what's on screen if it isn't saved anywhere.
        if (_host.Boards.HasUnsavedPins)
        {
            var choice = await new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = "Save your current pins first?",
                Content = $"Your snips and notes aren't in a board yet. Save them as a board before opening “{row.Name}”?\n\n"
                    + "If you don't, the snips close (they're still in your Pictures folder if saving is on) and notes are kept in the notes\\closed folder.",
                PrimaryButtonText = "Save as board…",
                SecondaryButtonText = "Don't save",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Primary,
            }.ShowAsync();

            if (choice == ContentDialogResult.None) return;
            if (choice == ContentDialogResult.Primary && !await SaveCurrentAsBoardAsync()) return;
        }

        await _host.Boards.OpenAsync(row.Id);
    }

    private async void Rename_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string id }) return;
        string? current = _host.Boards.List().FirstOrDefault(b => b.Id == id)?.Name;
        if (await AskNameAsync("Rename board", current) is { } name) _host.Boards.Rename(id, name);
    }

    private async void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string id }) return;
        string name = _host.Boards.List().FirstOrDefault(b => b.Id == id)?.Name ?? "this board";

        var choice = await new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = $"Delete “{name}”?",
            Content = id == _host.Boards.CurrentId
                ? "It's open right now: its snips and notes will close. This can't be undone."
                : "Its snips, notes and drawings will be gone. This can't be undone.",
            PrimaryButtonText = "Delete",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
        }.ShowAsync();

        if (choice == ContentDialogResult.Primary) _host.Boards.Delete(id);
    }

    // ---- Export as an image ------------------------------------------------------------------

    private async void ExportCurrent_Click(object sender, RoutedEventArgs e) => await ExportAsync(null);

    private async void ExportSaved_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string id }) await ExportAsync(id);
    }

    /// <summary>
    /// Saves or copies a board as one PNG: the open board / current pins (<paramref name="savedId"/> null or
    /// the open board) live from the screen, optionally with a snapshot of pinned windows; others from disk.
    /// </summary>
    private async Task ExportAsync(string? savedId)
    {
        var boards = _host.Boards;
        bool live = savedId is null || savedId == boards.CurrentId;
        string? name = live ? boards.CurrentName : boards.List().FirstOrDefault(b => b.Id == savedId)?.Name;

        var content = new StackPanel { Spacing = 8 };
        content.Children.Add(new TextBlock
        {
            Text = "Everything is laid out the way it is on screen, in one PNG image.",
            TextWrapping = TextWrapping.Wrap,
        });

        CheckBox? includeWindows = null;
        if (live && boards.HasPinnedWindows)
        {
            includeWindows = new CheckBox
            {
                Content = "Include pinned windows",
                IsChecked = _host.Settings.Current.ExportIncludesPinnedWindows,
            };
            content.Children.Add(includeWindows);
            content.Children.Add(new TextBlock
            {
                Text = "Adds a snapshot of how they look right now. It's only in the image: pinned windows never become part of a board.",
                TextWrapping = TextWrapping.Wrap,
                Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
                Opacity = 0.75,
                Margin = new Thickness(28, -8, 0, 0),
            });
        }

        var choice = await new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = name is null ? "Export what's pinned as an image" : $"Export “{name}” as an image",
            Content = content,
            PrimaryButtonText = "Save as…",
            SecondaryButtonText = "Copy",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
        }.ShowAsync();
        if (choice == ContentDialogResult.None) return;

        bool withWindows = includeWindows?.IsChecked == true;
        if (includeWindows is not null) _host.Settings.Update(s => s.ExportIncludesPinnedWindows = withWindows);

        string? path = null;
        if (choice == ContentDialogResult.Primary && (path = await PickImagePathAsync(name)) is null) return;

        try
        {
            byte[]? png = live ? await boards.RenderCurrentAsync(withWindows) : await boards.RenderSavedAsync(savedId!);
            if (png is null)
            {
                ShowExportResult(InfoBarSeverity.Warning, "Nothing to export", "There were no snips, notes or windows to put in the image.", null);
            }
            else if (path is null)
            {
                SnipStorage.CopyPngToClipboard(png);
                ShowExportResult(InfoBarSeverity.Success, "Copied", "The board image is on your clipboard.", null);
            }
            else
            {
                await File.WriteAllBytesAsync(path, png);
                ShowExportResult(InfoBarSeverity.Success, "Exported", $"Saved as {Path.GetFileName(path)}.", path);
            }
        }
        catch (Exception ex)
        {
            Log.Error("Board export failed", ex);
            ShowExportResult(InfoBarSeverity.Error, "Couldn't export the image", "The log in %LocalAppData%\\PinPoint\\logs has the details.", null);
        }
    }

    private async Task<string?> PickImagePathAsync(string? name)
    {
        var picker = new FileSavePicker
        {
            SuggestedStartLocation = PickerLocationId.PicturesLibrary,
            SuggestedFileName = SafeFileName(name ?? $"PinPoint {DateTime.Now:yyyy-MM-dd HH-mm}"),
        };
        picker.FileTypeChoices.Add("PNG image", [".png"]);
        if (XamlRoot?.ContentIslandEnvironment?.AppWindowId is { } id)
            WinRT.Interop.InitializeWithWindow.Initialize(picker, Win32Interop.GetWindowFromWindowId(id));

        return (await picker.PickSaveFileAsync())?.Path;
    }

    private static string SafeFileName(string name)
    {
        string safe = string.Concat(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? ' ' : c)).Trim();
        return safe.Length == 0 ? "Board" : safe;
    }

    private void ShowExportResult(InfoBarSeverity severity, string title, string message, string? path)
    {
        _lastExportPath = path;
        ShowExportButton.Visibility = path is null ? Visibility.Collapsed : Visibility.Visible;
        ExportInfo.Severity = severity;
        ExportInfo.Title = title;
        ExportInfo.Message = message;
        ExportInfo.IsOpen = true;
    }

    private void ShowExport_Click(object sender, RoutedEventArgs e)
    {
        if (_lastExportPath is null || !File.Exists(_lastExportPath)) return;
        Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{_lastExportPath}\"") { UseShellExecute = true });
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(_host.Boards.BoardsFolder);
            Process.Start(new ProcessStartInfo(_host.Boards.BoardsFolder) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Error("Could not open boards folder", ex);
        }
    }

    // ---- Helpers -----------------------------------------------------------------------------

    /// <summary>Asks for a name and saves what's pinned as a new board. False if cancelled.</summary>
    private async Task<bool> SaveCurrentAsBoardAsync()
    {
        if (!_host.Boards.HasPinsToSave) return false;
        if (await AskNameAsync("Save as board", $"Board {DateTime.Now:d MMM}") is not { } name) return false;
        if (await _host.Boards.SaveAsNewAsync(name)) return true;

        await new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "Couldn't save the board",
            Content = "Nothing was changed: your snips and notes are still pinned. The log in %LocalAppData%\\PinPoint\\logs has the details.",
            CloseButtonText = "OK",
        }.ShowAsync();
        return false;
    }

    private async Task<string?> AskNameAsync(string title, string? initial)
    {
        var box = new TextBox { Text = initial ?? string.Empty, PlaceholderText = "Board name", MaxLength = 80 };
        box.Loaded += (_, _) =>
        {
            box.Focus(FocusState.Programmatic);
            box.SelectAll();
        };

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = title,
            Content = box,
            PrimaryButtonText = "Save",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
        };
        box.TextChanged += (_, _) => dialog.IsPrimaryButtonEnabled = box.Text.Trim().Length > 0;

        return await dialog.ShowAsync() == ContentDialogResult.Primary && box.Text.Trim().Length > 0 ? box.Text.Trim() : null;
    }
}
