using PinPoint.Core.Hotkeys;

namespace PinPoint.Core.Tests;

public class HotkeyTests
{
    [Theory]
    [InlineData("Ctrl+Alt+P", HotkeyModifiers.Control | HotkeyModifiers.Alt, 0x50)]
    [InlineData("ctrl + shift + s", HotkeyModifiers.Control | HotkeyModifiers.Shift, 0x53)]
    [InlineData("Win+Ctrl+F5", HotkeyModifiers.Win | HotkeyModifiers.Control, 0x74)]
    [InlineData("PrintScreen", HotkeyModifiers.None, 0x2C)]
    [InlineData("Alt+`", HotkeyModifiers.Alt, 0xC0)]
    [InlineData("Ctrl+-", HotkeyModifiers.Control, 0xBD)]
    public void Parses_valid_shortcuts(string text, HotkeyModifiers modifiers, int key)
    {
        Assert.True(Hotkey.TryParse(text, out var hotkey));
        Assert.Equal(new Hotkey(modifiers, key), hotkey);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("P")]            // letters need a modifier
    [InlineData("Ctrl")]         // modifier only
    [InlineData("Ctrl+A+B")]     // two keys
    [InlineData("Ctrl+Banana")]  // unknown key
    public void Rejects_invalid_shortcuts(string? text) => Assert.False(Hotkey.TryParse(text, out _));

    [Fact]
    public void Formats_in_canonical_order_and_round_trips()
    {
        var hotkey = Hotkey.Parse("shift+alt+ctrl+win+s")!.Value;
        Assert.Equal("Win+Ctrl+Alt+Shift+S", hotkey.ToString());
        Assert.Equal(hotkey, Hotkey.Parse(hotkey.ToString()));
    }

    [Fact]
    public void Every_named_key_round_trips()
    {
        for (int vk = 1; vk < 256; vk++)
        {
            if (KeyNames.GetName(vk) is not { } name) continue;
            Assert.Equal(vk, KeyNames.GetKey(name));
        }
    }
}
