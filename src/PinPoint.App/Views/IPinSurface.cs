using PinPoint.Core.Geometry;
using PinPoint.Core.Interop;

namespace PinPoint.Views;

/// <summary>Where a pin is and how big (its whole window, in screen pixels), plus a snip's zoom step: what undo puts back.</summary>
public readonly record struct PinLayout(PixelRect Window, int ZoomStep);

/// <summary>
/// A window PinPoint itself floats on top (a snip, a note…). PinManager drives every surface the same
/// way — re-raising, hide all, click-through, fading, undo — so a new pin kind only implements this.
/// </summary>
public interface IPinSurface
{
    nint Handle { get; }

    /// <summary>Client area in screen pixels (used to find the pin under the mouse).</summary>
    PixelRect ScreenBounds { get; }

    double PinOpacity { get; set; }

    bool IsClickThrough { get; set; }

    /// <summary>
    /// True while the user is working in this window (a menu is open, typing…). Re-ordering windows
    /// then would close menus or interrupt input, so PinManager holds off re-raising.
    /// </summary>
    bool IsInteracting { get; }

    void NudgeOpacity(int direction);

    void SetHidden(bool hidden);

    void RaiseTopmost();

    /// <summary>Shows the surface fully opaque for a screen capture; returns false if it already was.</summary>
    bool SetTemporarilyOpaque(bool opaque);

    /// <summary>Position, size (and zoom) right now.</summary>
    PinLayout Layout { get; }

    /// <summary>Puts the pin back exactly at <paramref name="layout"/> (undo/redo).</summary>
    void ApplyLayout(PinLayout layout);

    /// <summary>The window moved or resized (dragged, zoomed, resized from its edges...).</summary>
    event EventHandler? LayoutChanged;
}

/// <summary>Shared bits for implementing the layout part of <see cref="IPinSurface"/>.</summary>
public static class PinLayouts
{
    public static PixelRect WindowRect(nint hwnd)
    {
        Win32.GetWindowRect(hwnd, out var r);
        return PixelRect.FromRect(r);
    }

    public static void MoveWindow(nint hwnd, PixelRect window) =>
        Win32.SetWindowPos(hwnd, 0, window.X, window.Y, window.Width, window.Height,
            Win32.SWP_NOZORDER | Win32.SWP_NOACTIVATE | Win32.SWP_NOOWNERZORDER);
}
