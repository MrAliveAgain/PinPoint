using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using PinPoint.Commands;
using PinPoint.Core.Diagnostics;
using PinPoint.Core.Hotkeys;
using PinPoint.Core.Interop;
using PinPoint.Core.Shell;
using PinPoint.Helpers;
using PinPoint.Services;
using PinPoint.Views;

namespace PinPoint;

/// <summary>
/// Composition root: creates and wires every service, owns the tray icon, hotkeys and the
/// manager window. To add a feature, add a service here and register an <see cref="AppCommand"/>.
/// </summary>
public sealed class AppHost : IDisposable
{
    private readonly Dictionary<string, Hotkey> _hotkeyConflicts = new(StringComparer.Ordinal);
    private MessageWindow? _messageWindow;
    private TrayIcon? _tray;
    private ManagerWindow? _manager;
    private bool _disposed;

    public AppHost(DispatcherQueue dispatcher)
    {
        Current = this;
        Dispatcher = dispatcher;
        Settings = new SettingsService(dispatcher);
        Notifier = new Notifier(Settings);
        Storage = new SnipStorage(Settings);
        Ocr = new OcrService();
        Pins = new PinManager(dispatcher, Settings, Storage, Ocr, Notifier);
        Snips = new SnipService(Pins, Storage, Settings, Notifier);
        Boards = new BoardService(dispatcher, Pins, Settings);
        RegisterCommands();
    }

    /// <summary>For XAML pages, which the Frame constructs without arguments.</summary>
    public static AppHost Current { get; private set; } = null!;

    public DispatcherQueue Dispatcher { get; }

    public SettingsService Settings { get; }

    public Notifier Notifier { get; }

    public SnipStorage Storage { get; }

    public OcrService Ocr { get; }

    public PinManager Pins { get; }

    public SnipService Snips { get; }

    public BoardService Boards { get; }

    public CommandRegistry Commands { get; } = new();

    public HotkeyService? Hotkeys { get; private set; }

    /// <summary>Commands whose shortcut is taken by another app.</summary>
    public IReadOnlyDictionary<string, Hotkey> HotkeyConflicts => _hotkeyConflicts;

    public event EventHandler? HotkeysChanged;

    public void Start()
    {
        NativeTheme.EnableDarkMenus();

        _messageWindow = new MessageWindow();
        Hotkeys = new HotkeyService(_messageWindow);
        ApplyHotkeys();

        _tray = new TrayIcon(_messageWindow, WindowHelpers.IconPath, "PinPoint") { MenuFactory = BuildTrayMenu };
        _tray.Invoked += () => ShowManager();

        Pins.RestoreNotes(); // notes that were open last time come back where they were
        _ = Boards.RestoreLastAsync(); // and so does the board you were working on

        // Last line of defence: never leave other apps' windows topmost/transparent behind us.
        AppDomain.CurrentDomain.ProcessExit += (_, _) => RestoreExternalWindows();
    }

    /// <summary>(Re)registers every command's shortcut from settings.</summary>
    public void ApplyHotkeys()
    {
        if (Hotkeys is null) return;

        _hotkeyConflicts.Clear();
        foreach (var command in Commands.All)
        {
            var hotkey = Settings.GetHotkey(command);
            if (!Hotkeys.Register(command.Id, hotkey, command.Execute) && hotkey is { } taken)
                _hotkeyConflicts[command.Id] = taken;
        }

        HotkeysChanged?.Invoke(this, EventArgs.Empty);
    }

    public void ShowManager(string? page = null)
    {
        if (_disposed) return;

        if (_manager is null)
        {
            _manager = new ManagerWindow();
            _manager.Closed += OnManagerClosed;
        }

        _manager.ShowAndActivate(page);
    }

    public void Exit()
    {
        Log.Info("Exiting");
        Dispose();
        Application.Current.Exit();
    }

    public void RestoreExternalWindows()
    {
        try
        {
            Pins.RestoreExternalWindows();
        }
        catch (Exception ex)
        {
            Log.Error("Restore failed", ex);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        Boards.FlushForExit(); // before pins close: the open board is saved as it looks right now
        Settings.Flush();
        _manager?.Close();
        Pins.Dispose();
        Snips.Close();
        Notifier.Close();
        _tray?.Dispose();
        Hotkeys?.Dispose();
        _messageWindow?.Dispose();
    }

    private void RegisterCommands()
    {
        Commands.Add(new AppCommand
        {
            Id = CommandIds.PinActiveWindow,
            Title = "Pin active window",
            Description = "Keep the window you're using on top of everything. Press again to unpin.",
            Glyph = "",
            DefaultHotkey = "Ctrl+Alt+P",
            ShowInTray = false, // the tray itself is the active window when its menu is open
            Execute = Pins.TogglePinForegroundWindow,
        });

        Commands.Add(new AppCommand
        {
            Id = CommandIds.SnipAndPin,
            Title = "Snip & pin",
            Description = "Grab any part of the screen and pin it on top instantly.",
            Glyph = "",
            DefaultHotkey = "Ctrl+Alt+S",
            Execute = Snips.Start,
        });

        Commands.Add(new AppCommand
        {
            Id = CommandIds.NewNote,
            Title = "New note",
            Description = "Open a blank note that floats on top. It saves as you type and comes back after a restart.",
            Glyph = "",
            DefaultHotkey = "Ctrl+Alt+N",
            Execute = Pins.CreateNote,
        });

        Commands.Add(new AppCommand
        {
            Id = CommandIds.ToggleHideAll,
            Title = "Hide all pins",
            Description = "Clear every pin out of the way, then bring them all back.",
            Glyph = "",
            DefaultHotkey = "Ctrl+Alt+H",
            IsChecked = () => Pins.IsHidden,
            CanExecute = () => Pins.HasPins,
            Execute = Pins.ToggleHidden,
        });

        Commands.Add(new AppCommand
        {
            Id = CommandIds.ToggleClickThrough,
            Title = "Click-through pins",
            Description = "Let clicks pass through pins to the game underneath.",
            Glyph = "",
            DefaultHotkey = "Ctrl+Alt+G",
            IsChecked = () => Pins.IsClickThrough,
            CanExecute = () => Pins.HasPins,
            Execute = Pins.ToggleClickThrough,
        });

        Commands.Add(new AppCommand
        {
            Id = CommandIds.MoreTransparent,
            Title = "Fade pin",
            Description = "Press to make the pin under your mouse more see-through, or every pin if the mouse isn't over one. Works on pinned windows too. (On a snip you can also hold right-click and scroll.)",
            Glyph = "",
            DefaultHotkey = "Ctrl+Alt+Down",
            ShowInTray = false,
            Execute = () => Pins.NudgeOpacity(-1),
        });

        Commands.Add(new AppCommand
        {
            Id = CommandIds.MoreOpaque,
            Title = "Unfade pin",
            Description = "Press to make the pin under your mouse more solid again, or every pin if the mouse isn't over one.",
            Glyph = "",
            DefaultHotkey = "Ctrl+Alt+Up",
            ShowInTray = false,
            Execute = () => Pins.NudgeOpacity(+1),
        });

        Commands.Add(new AppCommand
        {
            Id = CommandIds.CloseAllSnips,
            Title = "Close all snips",
            Description = "Close every pinned snip (saved files are kept).",
            Glyph = "",
            CanExecute = () => Pins.HasSnips,
            Execute = Pins.CloseAllSnips,
        });
    }

    private List<TrayMenuItem> BuildTrayMenu()
    {
        var items = new List<TrayMenuItem>();
        foreach (var command in Commands.All.Where(c => c.ShowInTray))
        {
            string text = Settings.GetHotkey(command) is { } hotkey ? $"{command.Title}\t{hotkey.ToDisplayString()}" : command.Title;
            items.Add(new TrayMenuItem(text, Deferred(command.Execute),
                IsChecked: command.IsChecked?.Invoke() ?? false,
                IsEnabled: command.CanExecute?.Invoke() ?? true));
        }

        items.Add(TrayMenuItem.Separator);
        items.Add(new TrayMenuItem("Open snips folder", Deferred(Storage.OpenFolder)));
        items.Add(new TrayMenuItem(Boards.CurrentName is { } board ? $"Boards  ({board})" : "Boards", Deferred(() => ShowManager(ManagerWindow.BoardsPageTag))));
        items.Add(new TrayMenuItem("Open PinPoint", Deferred(() => ShowManager())));
        items.Add(new TrayMenuItem("Settings", Deferred(() => ShowManager(ManagerWindow.SettingsPageTag))));
        items.Add(TrayMenuItem.Separator);
        items.Add(new TrayMenuItem("Exit", Deferred(Exit)));
        return items;

        // Run after the modal menu loop has fully unwound (e.g. snipping needs the menu gone first).
        Action Deferred(Action action) => () => Dispatcher.TryEnqueue(DispatcherQueuePriority.Low, () => action());
    }

    private void OnManagerClosed(object sender, WindowEventArgs args)
    {
        _manager = null;
        if (_disposed || Settings.Current.HasShownTrayHint) return;

        Notifier.Show("", "PinPoint is still running", "It lives in the system tray. Right-click the icon to exit.", force: true);
        Settings.Update(s => s.HasShownTrayHint = true);
    }
}
