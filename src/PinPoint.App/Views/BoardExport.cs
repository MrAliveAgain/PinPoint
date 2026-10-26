using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using PinPoint.Core.Diagnostics;
using PinPoint.Services;
using Windows.Storage.Pickers;

namespace PinPoint.Views;

/// <summary>How an export went, for the caller to show (null result = cancelled).</summary>
public sealed record ExportOutcome(InfoBarSeverity Severity, string Title, string Message, string? SavedPath);

/// <summary>
/// "Export as image" for a board: asks how (pack together? include pinned windows?), then saves or copies
/// one PNG. Shared by the Boards page and the board preview window, so it takes the window to show in.
/// </summary>
public static class BoardExport
{
    /// <param name="savedId">A saved board, or null for what's pinned now (the open board counts as "now").</param>
    public static async Task<ExportOutcome?> RunAsync(XamlRoot xamlRoot, nint ownerWindow, string? savedId)
    {
        var host = AppHost.Current;
        var boards = host.Boards;
        var settings = host.Settings;
        bool live = savedId is null || savedId == boards.CurrentId;
        string? name = live ? boards.CurrentName : boards.List().FirstOrDefault(b => b.Id == savedId)?.Name;

        var content = new StackPanel { Spacing = 8 };
        content.Children.Add(new TextBlock
        {
            Text = "Everything goes into one PNG image, laid out the way it is on screen.",
            TextWrapping = TextWrapping.Wrap,
        });

        var packed = new CheckBox { Content = "Pack together (no empty space between pins)", IsChecked = settings.Current.ExportPacked };
        content.Children.Add(packed);

        CheckBox? includeWindows = null;
        if (live && boards.HasPinnedWindows)
        {
            includeWindows = new CheckBox { Content = "Include pinned windows", IsChecked = settings.Current.ExportIncludesPinnedWindows };
            content.Children.Add(includeWindows);
            content.Children.Add(Caption("Adds a snapshot of how they look right now. It's only in the image: pinned windows never become part of a board."));
        }

        var choice = await new ContentDialog
        {
            XamlRoot = xamlRoot,
            Title = name is null ? "Export what's pinned as an image" : $"Export “{name}” as an image",
            Content = content,
            PrimaryButtonText = "Save as…",
            SecondaryButtonText = "Copy",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
        }.ShowAsync();
        if (choice == ContentDialogResult.None) return null;

        bool pack = packed.IsChecked == true;
        bool withWindows = includeWindows?.IsChecked == true;
        settings.Update(s =>
        {
            s.ExportPacked = pack;
            if (includeWindows is not null) s.ExportIncludesPinnedWindows = withWindows;
        });

        string? path = null;
        if (choice == ContentDialogResult.Primary)
        {
            path = await PickPngAsync(ownerWindow, name ?? $"PinPoint {DateTime.Now:yyyy-MM-dd HH-mm}");
            if (path is null) return null;
        }

        try
        {
            byte[]? png = live ? await boards.RenderCurrentAsync(withWindows, pack) : await boards.RenderSavedAsync(savedId!, pack);
            if (png is null) return new(InfoBarSeverity.Warning, "Nothing to export", "There were no snips, notes or windows to put in the image.", null);

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
        Margin = new Thickness(28, -8, 0, 0),
    };
}
