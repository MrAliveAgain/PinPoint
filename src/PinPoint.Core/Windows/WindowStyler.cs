using PinPoint.Core.Interop;

namespace PinPoint.Core.Windows;

/// <summary>Low-level window tweaks shared by external pins and PinPoint's own pin windows.</summary>
public static class WindowStyler
{
    private const uint ZOrderOnly = Win32.SWP_NOMOVE | Win32.SWP_NOSIZE | Win32.SWP_NOACTIVATE | Win32.SWP_NOOWNERZORDER;

    public static bool IsTopmost(nint hwnd) => (Win32.GetExStyle(hwnd) & Win32.WS_EX_TOPMOST) != 0;

    public static bool SetTopmost(nint hwnd, bool topmost) =>
        Win32.SetWindowPos(hwnd, topmost ? Win32.HWND_TOPMOST : Win32.HWND_NOTOPMOST, 0, 0, 0, 0, ZOrderOnly);

    /// <summary>Moves a topmost window back to the top of the topmost band without activating it.</summary>
    public static void RaiseTopmost(nint hwnd) => Win32.SetWindowPos(hwnd, Win32.HWND_TOPMOST, 0, 0, 0, 0, ZOrderOnly);

    public static bool HasExStyle(nint hwnd, long flag) => (Win32.GetExStyle(hwnd) & flag) != 0;

    public static void SetExStyleFlag(nint hwnd, long flag, bool enabled)
    {
        long style = Win32.GetExStyle(hwnd);
        long updated = enabled ? style | flag : style & ~flag;
        if (updated != style) Win32.SetExStyle(hwnd, updated);
    }

    /// <summary>Whole-window alpha via WS_EX_LAYERED. 255 = opaque.</summary>
    public static bool SetAlpha(nint hwnd, byte alpha)
    {
        SetExStyleFlag(hwnd, Win32.WS_EX_LAYERED, true);
        return Win32.SetLayeredWindowAttributes(hwnd, 0, alpha, Win32.LWA_ALPHA);
    }

    /// <summary>Drops WS_EX_LAYERED again and forces a repaint (required by Win32 after removing the style).</summary>
    public static void ClearLayered(nint hwnd)
    {
        if (!HasExStyle(hwnd, Win32.WS_EX_LAYERED)) return;
        SetExStyleFlag(hwnd, Win32.WS_EX_LAYERED, false);
        Win32.RedrawWindow(hwnd, 0, 0, Win32.RDW_ERASE | Win32.RDW_INVALIDATE | Win32.RDW_FRAME | Win32.RDW_ALLCHILDREN);
    }

    /// <summary>Win11 DWM border colour (COLORREF 0x00BBGGRR); null restores the system default.</summary>
    public static void SetBorderColor(nint hwnd, uint? colorRef) =>
        Win32.SetDwmUInt(hwnd, Win32.DWMWA_BORDER_COLOR, colorRef ?? Win32.DWMWA_COLOR_DEFAULT);

    /// <summary>Cloaked windows keep rendering but DWM doesn't show them — used to avoid first-frame flashes.</summary>
    public static void SetCloaked(nint hwnd, bool cloaked) => Win32.SetDwmInt(hwnd, Win32.DWMWA_CLOAK, cloaked ? 1 : 0);

    public static unsafe bool IsCloaked(nint hwnd)
    {
        int cloaked = 0;
        return Win32.DwmGetWindowAttribute(hwnd, Win32.DWMWA_CLOAKED, &cloaked, sizeof(int)) == 0 && cloaked != 0;
    }

    public static uint ToColorRef(byte r, byte g, byte b) => (uint)(r | (g << 8) | (b << 16));
}
