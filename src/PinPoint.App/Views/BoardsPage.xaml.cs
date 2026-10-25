using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using PinPoint.Core.Boards;
using PinPoint.Core.Diagnostics;

namespace PinPoint.Views;

/// <summary>One saved board in the list.</summary>
public sealed class BoardRow(BoardInfo info, bool isCurrent)
{
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

        var rows = boards.List().Select(b => new BoardRow(b, b.Id == boards.CurrentId)).ToList();
        BoardList.ItemsSource = rows;
        EmptyBoards.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
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
        await _host.Boards.SaveAsNewAsync(name);
        return true;
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
