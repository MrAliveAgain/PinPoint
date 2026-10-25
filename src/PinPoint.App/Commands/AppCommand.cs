namespace PinPoint.Commands;

/// <summary>
/// A user-invokable action. Registering one gives it a rebindable global shortcut on the
/// Settings page and (optionally) a tray-menu entry — the main extension point for new features.
/// </summary>
public sealed class AppCommand
{
    /// <summary>Stable id; used as the settings key, so never rename a shipped one.</summary>
    public required string Id { get; init; }

    public required string Title { get; init; }

    public required string Description { get; init; }

    /// <summary>Segoe Fluent Icons glyph.</summary>
    public string Glyph { get; init; } = "";

    public string? DefaultHotkey { get; init; }

    public bool ShowInTray { get; init; } = true;

    /// <summary>For toggle commands: current state (shown as a check mark in the tray).</summary>
    public Func<bool>? IsChecked { get; init; }

    public Func<bool>? CanExecute { get; init; }

    public required Action Execute { get; init; }
}

public static class CommandIds
{
    public const string PinActiveWindow = "pinActiveWindow";
    public const string SnipAndPin = "snipAndPin";
    public const string NewNote = "newNote";
    public const string ToggleHideAll = "toggleHideAll";
    public const string ToggleClickThrough = "toggleClickThrough";
    public const string CloseAllSnips = "closeAllSnips";
    public const string MoreOpaque = "pinsMoreOpaque";
    public const string MoreTransparent = "pinsMoreTransparent";
}

public sealed class CommandRegistry
{
    private readonly List<AppCommand> _commands = [];

    public IReadOnlyList<AppCommand> All => _commands;

    public void Add(AppCommand command)
    {
        if (_commands.Exists(c => c.Id == command.Id))
            throw new InvalidOperationException($"Command '{command.Id}' is already registered.");
        _commands.Add(command);
    }

    public AppCommand Get(string id) => _commands.Find(c => c.Id == id)
        ?? throw new KeyNotFoundException($"Unknown command '{id}'.");
}
