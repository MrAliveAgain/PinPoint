using Microsoft.UI.Dispatching;
using PinPoint.Commands;
using PinPoint.Core;
using PinPoint.Core.Hotkeys;
using PinPoint.Core.Settings;

namespace PinPoint.Services;

/// <summary>Owns the live <see cref="AppSettings"/>; mutations go through <see cref="Update"/> and are saved debounced.</summary>
public sealed class SettingsService
{
    private readonly SettingsStore _store = new(AppPaths.SettingsFile);
    private readonly DispatcherQueueTimer _saveTimer;

    public SettingsService(DispatcherQueue dispatcher)
    {
        Current = _store.Load();
        _saveTimer = dispatcher.CreateTimer();
        _saveTimer.Interval = TimeSpan.FromMilliseconds(400);
        _saveTimer.IsRepeating = false;
        _saveTimer.Tick += (_, _) => _store.Save(Current);
    }

    public AppSettings Current { get; }

    public event EventHandler? Changed;

    public string SettingsFilePath => _store.FilePath;

    public string SnipFolder => string.IsNullOrWhiteSpace(Current.SnipFolder) ? AppPaths.DefaultSnipDirectory : Current.SnipFolder;

    public void Update(Action<AppSettings> change)
    {
        change(Current);
        Changed?.Invoke(this, EventArgs.Empty);
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    public void Flush()
    {
        _saveTimer.Stop();
        _store.Save(Current);
    }

    /// <summary>The effective shortcut for a command (user override, else default; null = unassigned).</summary>
    public Hotkey? GetHotkey(AppCommand command) =>
        Current.Hotkeys.TryGetValue(command.Id, out string? text) ? Hotkey.Parse(text) : Hotkey.Parse(command.DefaultHotkey);

    public void SetHotkey(AppCommand command, Hotkey? hotkey) =>
        Update(s => s.Hotkeys[command.Id] = hotkey?.ToString() ?? string.Empty);

    public void ResetHotkey(AppCommand command) => Update(s => s.Hotkeys.Remove(command.Id));

    /// <summary>Highlight border as COLORREF, or null when disabled.</summary>
    public uint? HighlightColorRef =>
        Current.HighlightPinnedWindows && HexColor.TryParse(Current.HighlightColor, out byte r, out byte g, out byte b)
            ? Core.Windows.WindowStyler.ToColorRef(r, g, b)
            : null;
}
