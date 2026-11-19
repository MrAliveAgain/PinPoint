using System.Runtime.InteropServices;

namespace PinPoint.Core.Interop;

public static unsafe partial class Win32
{
    /// <summary>A monitor's DPI (type 0 = effective DPI, what scaling uses). Returns an HRESULT (0 = ok).</summary>
    [LibraryImport("shcore.dll")]
    public static partial int GetDpiForMonitor(nint hmonitor, int dpiType, out uint dpiX, out uint dpiY);

    /// <summary>Asks for 1 ms timer resolution while an animation runs (pair with timeEndPeriod).</summary>
    [LibraryImport("winmm.dll")]
    public static partial uint timeBeginPeriod(uint uPeriod);

    [LibraryImport("winmm.dll")]
    public static partial uint timeEndPeriod(uint uPeriod);

    [LibraryImport("user32.dll")]
    public static partial nint BeginDeferWindowPos(int nNumWindows);

    [LibraryImport("user32.dll")]
    public static partial nint DeferWindowPos(nint hWinPosInfo, nint hWnd, nint hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool EndDeferWindowPos(nint hWinPosInfo);

    /// <summary>Moves several windows in one go (one redraw for all of them), keeping size and z-order.</summary>
    public static void MoveWindowsTogether(IReadOnlyList<(nint Hwnd, int X, int Y)> moves)
    {
        if (moves.Count == 0) return;
        const uint flags = SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_NOOWNERZORDER;
        nint batch = BeginDeferWindowPos(moves.Count);
        foreach (var (hwnd, x, y) in moves)
        {
            if (batch == 0) break;
            batch = DeferWindowPos(batch, hwnd, 0, x, y, 0, 0, flags);
        }

        if (batch != 0)
        {
            EndDeferWindowPos(batch);
            return;
        }

        // A window went away mid-batch: fall back to moving them one by one.
        foreach (var (hwnd, x, y) in moves)
        {
            if (IsWindow(hwnd)) SetWindowPos(hwnd, 0, x, y, 0, 0, flags);
        }
    }
}
