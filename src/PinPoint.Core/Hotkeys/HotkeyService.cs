using PinPoint.Core.Diagnostics;
using PinPoint.Core.Interop;

namespace PinPoint.Core.Hotkeys;

/// <summary>
/// System-wide hotkeys via RegisterHotKey. Works while games (borderless) have focus,
/// costs nothing at idle, and needs no keyboard hook.
/// </summary>
public sealed class HotkeyService : IDisposable
{
    private const uint MOD_NOREPEAT = 0x4000;

    private readonly MessageWindow _window;
    private readonly Dictionary<string, Registration> _byName = new(StringComparer.Ordinal);
    private readonly Dictionary<int, Registration> _byId = [];
    private int _nextId = 1;
    private bool _suspended;

    public HotkeyService(MessageWindow window)
    {
        _window = window;
        _window.AddHook(OnMessage);
    }

    /// <summary>
    /// Binds <paramref name="name"/> to <paramref name="hotkey"/>, replacing any previous binding.
    /// Pass null to unbind. Returns false if another app already owns the combination.
    /// </summary>
    public bool Register(string name, Hotkey? hotkey, Action callback)
    {
        Unregister(name);
        if (hotkey is not { IsValid: true } key) return true;

        var registration = new Registration(_nextId++, name, key, callback);
        _byName[name] = registration;
        _byId[registration.Id] = registration;

        return _suspended || Activate(registration);
    }

    public void Unregister(string name)
    {
        if (!_byName.Remove(name, out var registration)) return;
        _byId.Remove(registration.Id);
        Deactivate(registration);
    }

    /// <summary>Temporarily releases every hotkey (e.g. while the user records a new shortcut).</summary>
    public void Suspend()
    {
        if (_suspended) return;
        _suspended = true;
        foreach (var registration in _byName.Values) Deactivate(registration);
    }

    public void Resume()
    {
        if (!_suspended) return;
        _suspended = false;
        foreach (var registration in _byName.Values) Activate(registration);
    }

    public void Dispose()
    {
        foreach (var registration in _byName.Values) Deactivate(registration);
        _byName.Clear();
        _byId.Clear();
        _window.RemoveHook(OnMessage);
    }

    private bool Activate(Registration registration)
    {
        registration.IsActive = Win32.RegisterHotKey(_window.Handle, registration.Id,
            (uint)registration.Hotkey.Modifiers | MOD_NOREPEAT, (uint)registration.Hotkey.Key);

        if (!registration.IsActive)
            Log.Warn($"Could not register {registration.Hotkey} for '{registration.Name}' (in use by another app?)");

        return registration.IsActive;
    }

    private void Deactivate(Registration registration)
    {
        if (!registration.IsActive) return;
        Win32.UnregisterHotKey(_window.Handle, registration.Id);
        registration.IsActive = false;
    }

    private bool OnMessage(uint msg, nint wParam, nint lParam, out nint result)
    {
        result = 0;
        if (msg != Win32.WM_HOTKEY || !_byId.TryGetValue((int)wParam, out var registration)) return false;

        try
        {
            registration.Callback();
        }
        catch (Exception ex)
        {
            Log.Error($"Hotkey '{registration.Name}' failed", ex);
        }

        return true;
    }

    private sealed class Registration(int id, string name, Hotkey hotkey, Action callback)
    {
        public int Id { get; } = id;
        public string Name { get; } = name;
        public Hotkey Hotkey { get; } = hotkey;
        public Action Callback { get; } = callback;
        public bool IsActive { get; set; }
    }
}
