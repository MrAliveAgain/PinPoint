using PinPoint.Core.Geometry;

namespace PinPoint.Views;

/// <summary>
/// A window PinPoint itself floats on top (a snip, a note…). PinManager drives every surface the same
/// way — re-raising, hide all, click-through, fading — so a new pin kind only implements this.
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
}
