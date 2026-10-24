namespace PinPoint.Core.Hotkeys;

/// <summary>Values match the Win32 MOD_* flags so they can be passed straight to RegisterHotKey.</summary>
[Flags]
public enum HotkeyModifiers
{
    None = 0,
    Alt = 0x1,
    Control = 0x2,
    Shift = 0x4,
    Win = 0x8,
}

/// <summary>A global keyboard shortcut, e.g. Ctrl+Alt+P. Serialised as its display string.</summary>
public readonly record struct Hotkey(HotkeyModifiers Modifiers, int Key)
{
    /// <summary>A usable shortcut: a real key plus at least one modifier (F-keys etc. may stand alone).</summary>
    public bool IsValid =>
        Key != 0 &&
        !KeyNames.IsModifier(Key) &&
        KeyNames.GetName(Key) is not null &&
        (Modifiers != HotkeyModifiers.None || KeyNames.CanStandAlone(Key));

    /// <summary>Display parts in canonical order, e.g. ["Ctrl", "Alt", "P"].</summary>
    public IReadOnlyList<string> Parts
    {
        get
        {
            var parts = new List<string>(5);
            if (Modifiers.HasFlag(HotkeyModifiers.Win)) parts.Add("Win");
            if (Modifiers.HasFlag(HotkeyModifiers.Control)) parts.Add("Ctrl");
            if (Modifiers.HasFlag(HotkeyModifiers.Alt)) parts.Add("Alt");
            if (Modifiers.HasFlag(HotkeyModifiers.Shift)) parts.Add("Shift");
            parts.Add(KeyNames.GetName(Key) ?? $"0x{Key:X2}");
            return parts;
        }
    }

    public override string ToString() => string.Join('+', Parts);

    public static Hotkey? Parse(string? text) => TryParse(text, out var hotkey) ? hotkey : null;

    public static bool TryParse(string? text, out Hotkey hotkey)
    {
        hotkey = default;
        if (string.IsNullOrWhiteSpace(text)) return false;

        var modifiers = HotkeyModifiers.None;
        int key = 0;

        foreach (string raw in text.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            switch (raw.ToLowerInvariant())
            {
                case "ctrl" or "control": modifiers |= HotkeyModifiers.Control; break;
                case "alt": modifiers |= HotkeyModifiers.Alt; break;
                case "shift": modifiers |= HotkeyModifiers.Shift; break;
                case "win" or "windows" or "meta": modifiers |= HotkeyModifiers.Win; break;
                default:
                    if (key != 0) return false; // two non-modifier keys
                    key = KeyNames.GetKey(raw);
                    if (key == 0) return false;
                    break;
            }
        }

        hotkey = new Hotkey(modifiers, key);
        return hotkey.IsValid;
    }
}
