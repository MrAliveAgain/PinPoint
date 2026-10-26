using PinPoint.Core.Geometry;
using PinPoint.Core.Interop;
using PinPoint.Core.Windows;

namespace PinPoint.Core.Capture;

/// <summary>
/// A picture of one window as it looks right now, even if other windows cover it (board images freeze
/// pinned windows this way). Uses PrintWindow's full-content mode, which also works for Chrome and
/// other GPU-drawn windows. Some games and video players may come out black.
/// </summary>
public static class WindowCapture
{
    /// <summary>The window's visible area (no invisible resize borders), or null if it isn't on screen.</summary>
    public static CapturedFrame? Capture(nint hwnd)
    {
        if (!Win32.IsWindowVisible(hwnd) || Win32.IsIconic(hwnd) || WindowStyler.IsCloaked(hwnd)) return null;
        if (!Win32.GetWindowRect(hwnd, out var rect)) return null;

        var window = PixelRect.FromRect(rect);
        var visible = WindowEnumerator.GetBounds(hwnd).Intersect(window);
        if (visible.IsEmpty) return null;

        bool printed = false;
        var frame = GdiScreenCapture.CaptureInto(window, (memoryDc, _) => printed = Win32.PrintWindow(hwnd, memoryDc, Win32.PW_RENDERFULLCONTENT));
        return printed ? frame.Crop(visible) : null;
    }
}
