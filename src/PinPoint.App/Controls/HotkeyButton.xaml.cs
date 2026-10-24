using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using PinPoint.Core.Hotkeys;
using PinPoint.Core.Interop;
using Windows.System;

namespace PinPoint.Controls;

/// <summary>Shows a shortcut as key caps; click it and press keys to record a new one.</summary>
public sealed partial class HotkeyButton : UserControl
{
    private const int VK_LWIN = 0x5B, VK_RWIN = 0x5C, VK_SHIFT = 0x10, VK_CONTROL = 0x11, VK_MENU = 0x12;

    private Hotkey? _hotkey;
    private bool _recording;

    public HotkeyButton()
    {
        InitializeComponent();
        Render();
    }

    /// <summary>New shortcut chosen by the user (null = cleared).</summary>
    public event EventHandler<Hotkey?>? HotkeyChanged;

    public event EventHandler? ResetRequested;

    /// <summary>True while listening; the host should suspend global hotkeys so they can be captured.</summary>
    public event EventHandler<bool>? RecordingChanged;

    public Hotkey? Hotkey
    {
        get => _hotkey;
        set
        {
            _hotkey = value;
            Render();
        }
    }

    public bool CanReset
    {
        get => ResetButton.Visibility == Visibility.Visible;
        set => ResetButton.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
    }

    private void RecordButton_Click(object sender, RoutedEventArgs e)
    {
        if (_recording) return;
        _recording = true;
        RecordingChanged?.Invoke(this, true);
        Render();
    }

    private void RecordButton_PreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (!_recording) return;
        e.Handled = true;

        var modifiers = CurrentModifiers();
        int key = (int)e.Key;

        if (modifiers == HotkeyModifiers.None && e.Key == VirtualKey.Escape)
        {
            StopRecording();
            return;
        }

        if (modifiers == HotkeyModifiers.None && e.Key is VirtualKey.Back or VirtualKey.Delete)
        {
            StopRecording();
            Hotkey = null;
            HotkeyChanged?.Invoke(this, null);
            return;
        }

        if (KeyNames.IsModifier(key))
        {
            Render(modifiers);
            return;
        }

        var candidate = new Hotkey(modifiers, key);
        if (!candidate.IsValid)
        {
            Placeholder.Text = "Add Ctrl, Alt, Shift or Win";
            return;
        }

        StopRecording();
        Hotkey = candidate;
        HotkeyChanged?.Invoke(this, candidate);
    }

    private void RecordButton_PreviewKeyUp(object sender, KeyRoutedEventArgs e)
    {
        if (!_recording) return;
        e.Handled = true;
        Render(CurrentModifiers());
    }

    private void RecordButton_LostFocus(object sender, RoutedEventArgs e) => StopRecording();

    private void ResetButton_Click(object sender, RoutedEventArgs e)
    {
        StopRecording();
        ResetRequested?.Invoke(this, EventArgs.Empty);
    }

    private void StopRecording()
    {
        if (!_recording) return;
        _recording = false;
        RecordingChanged?.Invoke(this, false);
        Render();
    }

    private void Render(HotkeyModifiers? pressed = null)
    {
        if (_recording)
        {
            var parts = pressed is { } m ? Core.Hotkeys.Hotkey.ModifierNames(m) : [];
            KeyCaps.ItemsSource = parts;
            Placeholder.Text = parts.Count == 0 ? "Press a shortcut…" : string.Empty;
            return;
        }

        KeyCaps.ItemsSource = _hotkey?.DisplayParts;
        Placeholder.Text = _hotkey is null ? "Not set" : string.Empty;
    }

    private static HotkeyModifiers CurrentModifiers()
    {
        var modifiers = HotkeyModifiers.None;
        if (Win32.IsKeyDown(VK_CONTROL)) modifiers |= HotkeyModifiers.Control;
        if (Win32.IsKeyDown(VK_MENU)) modifiers |= HotkeyModifiers.Alt;
        if (Win32.IsKeyDown(VK_SHIFT)) modifiers |= HotkeyModifiers.Shift;
        if (Win32.IsKeyDown(VK_LWIN) || Win32.IsKeyDown(VK_RWIN)) modifiers |= HotkeyModifiers.Win;
        return modifiers;
    }
}
