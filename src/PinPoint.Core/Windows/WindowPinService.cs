using PinPoint.Core.Diagnostics;
using PinPoint.Core.Interop;

namespace PinPoint.Core.Windows;

public enum PinResult
{
    Pinned,
    Unpinned,
    NotPinnable,
    Failed,
}

/// <summary>
/// Pins other apps' windows on top. Every change is tracked so unpinning (or exiting PinPoint)
/// restores the window exactly as it was.
/// </summary>
public sealed class WindowPinService
{
    private readonly List<PinnedWindow> _pins = [];
    private uint? _highlight;

    public event Action<PinnedWindow>? Pinned;

    public event Action<PinnedWindow>? Unpinned;

    public IReadOnlyList<PinnedWindow> Pins => _pins;

    /// <summary>Border colour applied to pinned windows (COLORREF), or null for none.</summary>
    public uint? HighlightColor
    {
        get => _highlight;
        set
        {
            _highlight = value;
            foreach (var pin in _pins) WindowStyler.SetBorderColor(pin.Handle, value);
        }
    }

    public PinnedWindow? Find(nint hwnd) => _pins.Find(p => p.Handle == hwnd);

    public PinResult Toggle(nint hwnd)
    {
        hwnd = Normalize(hwnd);
        return Find(hwnd) is { } pin ? (Unpin(pin) ? PinResult.Unpinned : PinResult.Failed) : Pin(hwnd).Result;
    }

    public (PinResult Result, PinnedWindow? Pin) Pin(nint hwnd)
    {
        hwnd = Normalize(hwnd);
        if (Find(hwnd) is { } existing) return (PinResult.Pinned, existing);
        if (!WindowEnumerator.IsPinnable(hwnd) || Win32.GetProcessId(hwnd) == Environment.ProcessId)
            return (PinResult.NotPinnable, null);

        long ex = Win32.GetExStyle(hwnd);
        var pin = new PinnedWindow(hwnd, Win32.GetWindowText(hwnd), WindowEnumerator.GetProcessName(Win32.GetProcessId(hwnd)),
            wasTopmost: (ex & Win32.WS_EX_TOPMOST) != 0,
            wasLayered: (ex & Win32.WS_EX_LAYERED) != 0)
        {
            WasTransparent = (ex & Win32.WS_EX_TRANSPARENT) != 0,
        };

        // Fails for elevated windows when PinPoint isn't elevated (UIPI).
        if (!WindowStyler.SetTopmost(hwnd, true))
        {
            Log.Warn($"SetWindowPos(TOPMOST) failed for '{pin.Title}' ({pin.ProcessName})");
            return (PinResult.Failed, null);
        }

        if (_highlight is { } color) WindowStyler.SetBorderColor(hwnd, color);

        _pins.Add(pin);
        Log.Info($"Pinned '{pin.Title}' ({pin.ProcessName})");
        Pinned?.Invoke(pin);
        return (PinResult.Pinned, pin);
    }

    public bool Unpin(PinnedWindow pin)
    {
        if (!_pins.Remove(pin)) return false;
        Restore(pin);
        Log.Info($"Unpinned '{pin.Title}'");
        Unpinned?.Invoke(pin);
        return true;
    }

    public void UnpinAll()
    {
        foreach (var pin in _pins.ToArray()) Unpin(pin);
    }

    public bool SetOpacity(PinnedWindow pin, double opacity)
    {
        if (!pin.SupportsOpacity || !Win32.IsWindow(pin.Handle)) return false;

        pin.Opacity = Math.Clamp(opacity, 0.1, 1.0);
        ApplyLayering(pin);
        return true;
    }

    public void SetClickThrough(PinnedWindow pin, bool enabled)
    {
        if (!pin.SupportsOpacity || pin.IsClickThrough == enabled) return;

        pin.IsClickThrough = enabled;
        WindowStyler.SetExStyleFlag(pin.Handle, Win32.WS_EX_TRANSPARENT, enabled || pin.WasTransparent);
        ApplyLayering(pin);
    }

    /// <summary>Minimises (or restores) every pinned window without activating anything.</summary>
    public void SetHidden(bool hidden)
    {
        foreach (var pin in _pins)
        {
            if (pin.IsHidden == hidden) continue;
            pin.IsHidden = hidden;
            Win32.ShowWindow(pin.Handle, hidden ? Win32.SW_SHOWMINNOACTIVE : Win32.SW_SHOWNOACTIVATE);
            if (!hidden) WindowStyler.RaiseTopmost(pin.Handle);
        }
    }

    /// <summary>Re-asserts z-order; games that are themselves topmost can otherwise cover pins.</summary>
    public void RaiseAll()
    {
        foreach (var pin in _pins)
        {
            if (!pin.IsHidden) WindowStyler.RaiseTopmost(pin.Handle);
        }
    }

    /// <summary>Drops pins whose windows were closed and refreshes titles. Returns true if anything changed.</summary>
    public bool Prune()
    {
        bool changed = false;
        foreach (var pin in _pins.ToArray())
        {
            if (!Win32.IsWindow(pin.Handle))
            {
                _pins.Remove(pin);
                Unpinned?.Invoke(pin);
                changed = true;
                continue;
            }

            string title = Win32.GetWindowText(pin.Handle);
            if (title.Length > 0 && title != pin.Title)
            {
                pin.Title = title;
                changed = true;
            }
        }

        return changed;
    }

    private void ApplyLayering(PinnedWindow pin)
    {
        bool needsLayer = pin.Opacity < 0.999 || pin.IsClickThrough;
        if (needsLayer)
            WindowStyler.SetAlpha(pin.Handle, (byte)Math.Round(pin.Opacity * 255));
        else
            WindowStyler.ClearLayered(pin.Handle);
    }

    private static void Restore(PinnedWindow pin)
    {
        if (!Win32.IsWindow(pin.Handle)) return;

        try
        {
            if (pin.IsHidden) Win32.ShowWindow(pin.Handle, Win32.SW_SHOWNOACTIVATE);
            if (pin.IsClickThrough && !pin.WasTransparent)
                WindowStyler.SetExStyleFlag(pin.Handle, Win32.WS_EX_TRANSPARENT, false);
            if (pin.SupportsOpacity && (pin.Opacity < 0.999 || pin.IsClickThrough))
                WindowStyler.ClearLayered(pin.Handle);

            WindowStyler.SetBorderColor(pin.Handle, null);
            if (!pin.WasTopmost) WindowStyler.SetTopmost(pin.Handle, false);
        }
        catch (Exception ex)
        {
            Log.Error($"Failed to restore '{pin.Title}'", ex);
        }
    }

    /// <summary>Hotkeys may report a child control; always operate on the top-level window.</summary>
    private static nint Normalize(nint hwnd)
    {
        nint root = Win32.GetAncestor(hwnd, Win32.GA_ROOT);
        return root != 0 ? root : hwnd;
    }
}
