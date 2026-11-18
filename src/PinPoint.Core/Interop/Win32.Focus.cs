using System.Runtime.InteropServices;

namespace PinPoint.Core.Interop;

public static unsafe partial class Win32
{
    private const byte VK_MENU = 0x12;
    private const uint KEYEVENTF_KEYUP = 0x0002;

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool AttachThreadInput(uint idAttach, uint idAttachTo, [MarshalAs(UnmanagedType.Bool)] bool fAttach);

    [LibraryImport("kernel32.dll")]
    public static partial uint GetCurrentThreadId();

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool BringWindowToTop(nint hWnd);

    [LibraryImport("user32.dll")]
    private static partial void keybd_event(byte bVk, byte bScan, uint dwFlags, nuint dwExtraInfo);

    /// <summary>
    /// Gives <paramref name="hwnd"/> keyboard focus even when Windows' foreground lock would refuse a plain
    /// SetForegroundWindow (e.g. we were clicked through a window that never activates, like a snip):
    /// borrows the current foreground thread's input state for the switch, then, if that's still refused,
    /// taps Alt (which lifts the lock) and tries once more. Returns whether it ended up in front.
    /// </summary>
    public static bool ForceForeground(nint hwnd)
    {
        if (GetForegroundWindow() == hwnd) return true;
        if (SetForegroundWindow(hwnd) && GetForegroundWindow() == hwnd) return true;

        nint foreground = GetForegroundWindow();
        uint current = GetCurrentThreadId();
        uint target = foreground != 0 ? GetWindowThreadProcessId(foreground, out _) : 0;
        bool attached = target != 0 && target != current && AttachThreadInput(current, target, true);
        try
        {
            BringWindowToTop(hwnd);
            SetForegroundWindow(hwnd);
        }
        finally
        {
            if (attached) AttachThreadInput(current, target, false);
        }

        if (GetForegroundWindow() == hwnd) return true;

        keybd_event(VK_MENU, 0, 0, 0);
        keybd_event(VK_MENU, 0, KEYEVENTF_KEYUP, 0);
        SetForegroundWindow(hwnd);
        return GetForegroundWindow() == hwnd;
    }
}
