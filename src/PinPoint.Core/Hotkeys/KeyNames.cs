using System.Collections.Frozen;

namespace PinPoint.Core.Hotkeys;

/// <summary>Maps Win32 virtual-key codes to stable, human-readable names (and back).</summary>
public static class KeyNames
{
    private static readonly FrozenDictionary<int, string> s_names = BuildNames();
    private static readonly FrozenDictionary<string, int> s_keys =
        s_names.ToFrozenDictionary(kv => kv.Value, kv => kv.Key, StringComparer.OrdinalIgnoreCase);

    public static string? GetName(int vk) => s_names.GetValueOrDefault(vk);

    public static int GetKey(string name) => s_keys.GetValueOrDefault(name);

    public static bool IsModifier(int vk) => vk is
        0x10 or 0x11 or 0x12 or            // Shift, Ctrl, Alt
        >= 0xA0 and <= 0xA5 or             // L/R Shift, Ctrl, Alt
        0x5B or 0x5C;                      // L/R Win

    /// <summary>Keys that are safe to bind without a modifier (they rarely mean anything on their own).</summary>
    public static bool CanStandAlone(int vk) => vk is
        >= 0x70 and <= 0x87 or             // F1–F24
        0x2C or 0x13 or 0x91;              // PrintScreen, Pause, ScrollLock

    private static FrozenDictionary<int, string> BuildNames()
    {
        var names = new Dictionary<int, string>
        {
            [0x08] = "Backspace", [0x09] = "Tab", [0x0D] = "Enter", [0x13] = "Pause", [0x1B] = "Esc",
            [0x20] = "Space", [0x21] = "PageUp", [0x22] = "PageDown", [0x23] = "End", [0x24] = "Home",
            [0x25] = "Left", [0x26] = "Up", [0x27] = "Right", [0x28] = "Down",
            [0x2C] = "PrintScreen", [0x2D] = "Insert", [0x2E] = "Delete", [0x91] = "ScrollLock",
            [0x6A] = "NumMultiply", [0x6B] = "NumAdd", [0x6D] = "NumSubtract", [0x6E] = "NumDecimal", [0x6F] = "NumDivide",
            [0xBA] = ";", [0xBB] = "=", [0xBC] = ",", [0xBD] = "-", [0xBE] = ".", [0xBF] = "/",
            [0xC0] = "`", [0xDB] = "[", [0xDC] = "\\", [0xDD] = "]", [0xDE] = "'",
        };

        for (int i = 0; i <= 9; i++) names[0x30 + i] = ((char)('0' + i)).ToString();
        for (int i = 0; i < 26; i++) names[0x41 + i] = ((char)('A' + i)).ToString();
        for (int i = 0; i <= 9; i++) names[0x60 + i] = $"Num{i}";
        for (int i = 1; i <= 24; i++) names[0x6F + i] = $"F{i}";

        return names.ToFrozenDictionary();
    }
}
