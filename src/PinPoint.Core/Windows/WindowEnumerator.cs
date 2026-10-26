using System.Diagnostics;
using System.Runtime.InteropServices;
using PinPoint.Core.Geometry;
using PinPoint.Core.Interop;

namespace PinPoint.Core.Windows;

public sealed record WindowInfo(nint Handle, string Title, string ProcessName, PixelRect Bounds);

/// <summary>Finds top-level windows and monitors (z-order, top first).</summary>
public static unsafe class WindowEnumerator
{
    private static readonly HashSet<string> s_shellClasses = new(StringComparer.Ordinal)
    {
        "Progman", "WorkerW", "Shell_TrayWnd", "Shell_SecondaryTrayWnd", "Windows.UI.Core.CoreWindow",
        "XamlExplorerHostIslandWindow", "TopLevelWindowForOverflowXamlIsland",
    };

    /// <summary>Visible window and monitor rectangles the snip overlay can snap to, topmost first.</summary>
    public static List<PixelRect> GetSnapTargets(nint exclude)
    {
        var targets = new List<PixelRect>();
        foreach (nint hwnd in EnumerateTopLevel())
        {
            if (hwnd == exclude || !IsVisibleOnScreen(hwnd) || IsShell(hwnd)) continue;
            if (WindowStyler.HasExStyle(hwnd, Win32.WS_EX_TRANSPARENT)) continue; // click-through overlays

            var bounds = GetBounds(hwnd);
            if (bounds.Width > 8 && bounds.Height > 8) targets.Add(bounds);
        }

        targets.AddRange(GetMonitors()); // falls back to whole monitor over the desktop
        return targets;
    }

    /// <summary>Windows a user would recognise from Alt+Tab, excluding PinPoint's own.</summary>
    public static List<WindowInfo> GetPinnableWindows()
    {
        uint ownPid = (uint)Environment.ProcessId;
        var result = new List<WindowInfo>();
        var processNames = new Dictionary<uint, string>();

        foreach (nint hwnd in EnumerateTopLevel())
        {
            if (!IsPinnable(hwnd)) continue;
            uint pid = Win32.GetProcessId(hwnd);
            if (pid == ownPid) continue;

            string title = Win32.GetWindowText(hwnd);
            if (title.Length == 0) continue;

            if (!processNames.TryGetValue(pid, out string? processName))
                processNames[pid] = processName = GetProcessName(pid);

            result.Add(new WindowInfo(hwnd, title, processName, GetBounds(hwnd)));
        }

        return result;
    }

    /// <summary>True for normal app windows: visible, not cloaked, not the shell, not a tool/owned popup.</summary>
    public static bool IsPinnable(nint hwnd)
    {
        if (hwnd == 0 || !IsVisibleOnScreen(hwnd) || IsShell(hwnd)) return false;
        if (Win32.GetWindow(hwnd, Win32.GW_OWNER) != 0) return false;

        long ex = Win32.GetExStyle(hwnd);
        return (ex & Win32.WS_EX_TOOLWINDOW) == 0 || (ex & Win32.WS_EX_APPWINDOW) != 0;
    }

    public static bool IsShell(nint hwnd) =>
        hwnd == Win32.GetShellWindow() || s_shellClasses.Contains(Win32.GetClassName(hwnd));

    /// <summary>Visible bounds (DWM frame, without the invisible resize borders).</summary>
    public static PixelRect GetBounds(nint hwnd)
    {
        Win32.RECT rect;
        if (Win32.DwmGetWindowAttribute(hwnd, Win32.DWMWA_EXTENDED_FRAME_BOUNDS, &rect, (uint)sizeof(Win32.RECT)) != 0)
            Win32.GetWindowRect(hwnd, out rect);
        return PixelRect.FromRect(rect);
    }

    public static string GetProcessName(uint pid)
    {
        try
        {
            using var process = Process.GetProcessById((int)pid);
            return process.ProcessName;
        }
        catch
        {
            return string.Empty;
        }
    }

    public static List<PixelRect> GetMonitors()
    {
        var monitors = new List<PixelRect>();
        var handle = GCHandle.Alloc(monitors);
        try
        {
            Win32.EnumDisplayMonitors(0, 0, &OnMonitor, GCHandle.ToIntPtr(handle));
        }
        finally
        {
            handle.Free();
        }

        return monitors;
    }

    /// <summary>Full bounds and work area of the monitor nearest a screen point.</summary>
    public static (PixelRect Bounds, PixelRect WorkArea) GetMonitorAt(int x, int y)
    {
        nint monitor = Win32.MonitorFromPoint(new Win32.POINT { X = x, Y = y }, Win32.MONITOR_DEFAULTTONEAREST);
        var info = new Win32.MONITORINFO { cbSize = (uint)sizeof(Win32.MONITORINFO) };
        Win32.GetMonitorInfoW(monitor, ref info);
        return (PixelRect.FromRect(info.rcMonitor), PixelRect.FromRect(info.rcWork));
    }

    private static bool IsVisibleOnScreen(nint hwnd) =>
        Win32.IsWindowVisible(hwnd) && !Win32.IsIconic(hwnd) && !WindowStyler.IsCloaked(hwnd);

    /// <summary>Every top-level window, topmost first (EnumWindows goes in z-order).</summary>
    public static IReadOnlyList<nint> TopLevelInZOrder() => EnumerateTopLevel();

    private static List<nint> EnumerateTopLevel()
    {
        var handles = new List<nint>(256);
        var handle = GCHandle.Alloc(handles);
        try
        {
            Win32.EnumWindows(&OnWindow, GCHandle.ToIntPtr(handle));
        }
        finally
        {
            handle.Free();
        }

        return handles;
    }

    [UnmanagedCallersOnly]
    private static int OnWindow(nint hwnd, nint lParam)
    {
        ((List<nint>)GCHandle.FromIntPtr(lParam).Target!).Add(hwnd);
        return 1;
    }

    [UnmanagedCallersOnly]
    private static int OnMonitor(nint monitor, nint hdc, Win32.RECT* rect, nint lParam)
    {
        ((List<PixelRect>)GCHandle.FromIntPtr(lParam).Target!).Add(PixelRect.FromRect(*rect));
        return 1;
    }
}
