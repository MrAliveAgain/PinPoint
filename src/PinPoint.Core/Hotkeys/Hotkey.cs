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

    /// <summary>Canonical parts as stored in settings, e.g. ["Ctrl", "Alt", "Up"].</summary>
    public IReadOnlyList<string> Parts => [.. ModifierNames(Modifiers), KeyNames.GetName(Key) ?? $"0x{Key:X2}"];

    /// <summary>Parts for showing to people (key caps, menus), e.g. ["Ctrl", "Alt", "↑"].</summary>
    public IReadOnlyList<string> DisplayParts => [.. ModifierNames(Modifiers), KeyNames.GetDisplayName(Key) ?? $"0x{Key:X2}"];

    /// <summary>Settings form, e.g. "Ctrl+Alt+Up". Round-trips through <see cref="TryParse"/>.</summary>
    public override string ToString() => string.Join('+', Parts);

    /// <summary>Human form, e.g. "Ctrl+Alt+↑". Not parseable; use <see cref="ToString"/> to store.</summary>
    public string ToDisplayString() => string.Join('+', DisplayParts);

    /// <summary>Modifier names in canonical order, e.g. ["Ctrl", "Alt"].</summary>
    public static IReadOnlyList<string> ModifierNames(HotkeyModifiers modifiers)
    {
        var names = new List<string>(4);
        if (modifiers.HasFlag(HotkeyModifiers.Win)) names.Add("Win");
        if (modifiers.HasFlag(HotkeyModifiers.Control)) names.Add("Ctrl");
        if (modifiers.HasFlag(HotkeyModifiers.Alt)) names.Add("Alt");
        if (modifiers.HasFlag(HotkeyModifiers.Shift)) names.Add("Shift");
        return names;
    }

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
