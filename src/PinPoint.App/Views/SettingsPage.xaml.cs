using System.Diagnostics;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using PinPoint.Controls;
using PinPoint.Core;
using PinPoint.Core.Diagnostics;
using PinPoint.Core.Settings;
using PinPoint.Core.Shell;
using Windows.Storage.Pickers;
using Windows.UI;

namespace PinPoint.Views;

public sealed partial class SettingsPage : Page
{
    private static readonly (string Name, string Hex)[] s_highlightColors =
    [
        ("Sky blue", "#4CC2FF"),
        ("Mint", "#3DDC97"),
        ("Amber", "#FFB900"),
        ("Coral", "#FF6B5B"),
        ("Orchid", "#C27CF5"),
        ("White", "#FFFFFF"),
    ];

    private readonly AppHost _host = AppHost.Current;
    private bool _loading;

    public SettingsPage()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            _host.HotkeysChanged += OnHotkeysChanged;
            Load();
        };
        Unloaded += (_, _) => _host.HotkeysChanged -= OnHotkeysChanged;
    }

    private void OnHotkeysChanged(object? sender, EventArgs e) => BuildShortcutCards();

    private void Load()
    {
        _loading = true;
        try
        {
            var settings = _host.Settings.Current;
            SaveSnipsToggle.IsOn = settings.SaveSnips;
            ClipboardToggle.IsOn = settings.CopySnipsToClipboard;
            KeepFocusToggle.IsOn = !settings.PinsTakeFocus;
            HighlightToggle.IsOn = settings.HighlightPinnedWindows;
            NotificationsToggle.IsOn = settings.ShowNotifications;
            StartupToggle.IsOn = StartupRegistration.IsEnabled;
            FolderCard.Description = _host.Settings.SnipFolder;
            AboutCard.Description = $"Version {typeof(App).Assembly.GetName().Version?.ToString(3)} · Pin windows and snips on top of anything.";

            FadeModifierBox.ItemsSource = Services.SettingsService.FadeModifiers;
            FadeModifierBox.SelectedItem = Services.SettingsService.FadeModifiers.Contains(settings.FadeScrollModifier)
                ? settings.FadeScrollModifier
                : Services.SettingsService.FadeModifiers[0];

            BuildColorChoices(settings.HighlightColor);
            ColorBox.IsEnabled = settings.HighlightPinnedWindows;
            BuildShortcutCards();
        }
        finally
        {
            _loading = false;
        }
    }

    // ---- Shortcuts ---------------------------------------------------------------------------

    private void BuildShortcutCards()
    {
        ShortcutCards.Children.Clear();

        foreach (var command in _host.Commands.All)
        {
            var button = new HotkeyButton
            {
                Hotkey = _host.Settings.GetHotkey(command),
                CanReset = _host.Settings.Current.Hotkeys.ContainsKey(command.Id),
            };
            button.RecordingChanged += (_, recording) =>
            {
                if (recording) _host.Hotkeys?.Suspend();
                else _host.Hotkeys?.Resume();
            };
            button.HotkeyChanged += (_, hotkey) =>
            {
                _host.Settings.SetHotkey(command, hotkey);
                _host.ApplyHotkeys();
            };
            button.ResetRequested += (_, _) =>
            {
                _host.Settings.ResetHotkey(command);
                _host.ApplyHotkeys();
            };

            string description = _host.HotkeyConflicts.TryGetValue(command.Id, out var taken)
                ? $"{command.Description}\n⚠ {taken} is already used by another app."
                : command.Description;

            ShortcutCards.Children.Add(new SettingsCard
            {
                Glyph = command.Glyph,
                Header = command.Title,
                Description = description,
                Content = button,
            });
        }

        HotkeyWarning.IsOpen = _host.HotkeyConflicts.Count > 0;
    }

    // ---- Highlight colour --------------------------------------------------------------------

    private void BuildColorChoices(string current)
    {
        ColorBox.Items.Clear();
        var choices = s_highlightColors.ToList();
        if (!choices.Exists(c => string.Equals(c.Hex, current, StringComparison.OrdinalIgnoreCase)))
            choices.Add(("Custom", current));

        foreach (var (name, hex) in choices)
        {
            HexColor.TryParse(hex, out byte r, out byte g, out byte b);
            var item = new ComboBoxItem
            {
                Tag = hex,
                Content = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 8,
                    Children =
                    {
                        new Ellipse
                        {
                            Width = 12,
                            Height = 12,
                            Fill = new SolidColorBrush(Color.FromArgb(255, r, g, b)),
                            Stroke = new SolidColorBrush(Colors.Gray),
                            StrokeThickness = 0.5,
                            VerticalAlignment = VerticalAlignment.Center,
                        },
                        new TextBlock { Text = name },
                    },
                },
            };
            ColorBox.Items.Add(item);
            if (string.Equals(hex, current, StringComparison.OrdinalIgnoreCase)) ColorBox.SelectedItem = item;
        }
    }

    private void ColorBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || ColorBox.SelectedItem is not ComboBoxItem { Tag: string hex }) return;
        _host.Settings.Update(s => s.HighlightColor = hex);
    }

    private void FadeModifierBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (FadeModifierBox.SelectedItem is string modifier) Set(s => s.FadeScrollModifier = modifier);
    }

    // ---- Toggles -----------------------------------------------------------------------------

    private void SaveSnips_Toggled(object sender, RoutedEventArgs e) => Set(s => s.SaveSnips = SaveSnipsToggle.IsOn);

    private void Clipboard_Toggled(object sender, RoutedEventArgs e) => Set(s => s.CopySnipsToClipboard = ClipboardToggle.IsOn);

    private void KeepFocus_Toggled(object sender, RoutedEventArgs e) => Set(s => s.PinsTakeFocus = !KeepFocusToggle.IsOn);

    private void Notifications_Toggled(object sender, RoutedEventArgs e) => Set(s => s.ShowNotifications = NotificationsToggle.IsOn);

    private void Highlight_Toggled(object sender, RoutedEventArgs e)
    {
        ColorBox.IsEnabled = HighlightToggle.IsOn;
        Set(s => s.HighlightPinnedWindows = HighlightToggle.IsOn);
    }

    private void Startup_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        if (!StartupRegistration.SetEnabled(StartupToggle.IsOn))
        {
            _loading = true;
            StartupToggle.IsOn = StartupRegistration.IsEnabled;
            _loading = false;
        }
    }

    private void Set(Action<AppSettings> change)
    {
        if (!_loading) _host.Settings.Update(change);
    }

    // ---- Folders -----------------------------------------------------------------------------

    private async void ChangeFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new FolderPicker { SuggestedStartLocation = PickerLocationId.PicturesLibrary };
            picker.FileTypeFilter.Add("*");
            if (XamlRoot?.ContentIslandEnvironment?.AppWindowId is { } id)
                WinRT.Interop.InitializeWithWindow.Initialize(picker, Win32Interop.GetWindowFromWindowId(id));

            var folder = await picker.PickSingleFolderAsync();
            if (folder is null) return;

            _host.Settings.Update(s => s.SnipFolder = folder.Path);
            FolderCard.Description = folder.Path;
        }
        catch (Exception ex)
        {
            Log.Error("Folder picker failed", ex);
        }
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e) => _host.Storage.OpenFolder();

    private void OpenSettingsFile_Click(object sender, RoutedEventArgs e)
    {
        _host.Settings.Flush();
        Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{_host.Settings.SettingsFilePath}\"") { UseShellExecute = true });
    }

    private void OpenLogs_Click(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(Log.Directory);
        Process.Start(new ProcessStartInfo(Log.Directory) { UseShellExecute = true });
    }
}
