using System.Runtime.InteropServices;
using PinPoint.Core.Diagnostics;
using PinPoint.Core.Geometry;
using PinPoint.Core.Interop;

namespace PinPoint.Core.Capture;

/// <summary>
/// A monitor and its colour mode. <paramref name="AdvancedColor"/> is true with HDR on (or Auto Color Management),
/// when the desktop is composed in linear scRGB and GDI captures of it come out blown out.
/// <paramref name="SdrWhiteNits"/> is Windows' "SDR content brightness": how bright plain white is on that screen.
/// </summary>
public sealed record DisplayColorInfo(nint Monitor, PixelRect Bounds, bool AdvancedColor, float SdrWhiteNits);

public static unsafe class DisplayColor
{
    /// <summary>scRGB's reference white: 1.0 in a linear float frame means 80 nits.</summary>
    public const float ScRgbWhiteNits = 80;

    /// <summary>Every active monitor with its colour mode. Monitors the query can't match count as plain SDR.</summary>
    public static IReadOnlyList<DisplayColorInfo> Query()
    {
        var byDevice = QueryByGdiDeviceName();
        var result = new List<DisplayColorInfo>();
        foreach (nint monitor in MonitorHandles())
        {
            var info = new Win32.MONITORINFOEXW { cbSize = (uint)sizeof(Win32.MONITORINFOEXW) };
            if (!Win32.GetMonitorInfoExW(monitor, &info)) continue;

            string device = new(info.szDevice);
            var (advanced, white) = byDevice.TryGetValue(device, out var color) ? color : (false, ScRgbWhiteNits);
            result.Add(new DisplayColorInfo(monitor, PixelRect.FromRect(info.rcMonitor), advanced, white));
        }

        return result;
    }

    /// <summary>GDI device name ("\\.\DISPLAY1") → (HDR on, SDR white in nits), from the display configuration.</summary>
    private static Dictionary<string, (bool AdvancedColor, float SdrWhiteNits)> QueryByGdiDeviceName()
    {
        var map = new Dictionary<string, (bool, float)>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (Win32.GetDisplayConfigBufferSizes(Win32.QDC_ONLY_ACTIVE_PATHS, out uint pathCount, out uint modeCount) != 0) return map;

            var paths = new Win32.DISPLAYCONFIG_PATH_INFO[pathCount];
            var modes = new Win32.DISPLAYCONFIG_MODE_INFO[modeCount];
            fixed (Win32.DISPLAYCONFIG_PATH_INFO* p = paths)
            fixed (Win32.DISPLAYCONFIG_MODE_INFO* m = modes)
            {
                if (Win32.QueryDisplayConfig(Win32.QDC_ONLY_ACTIVE_PATHS, ref pathCount, p, ref modeCount, m, 0) != 0) return map;
            }

            foreach (var path in paths.AsSpan(0, (int)pathCount))
            {
                var name = new Win32.DISPLAYCONFIG_SOURCE_DEVICE_NAME();
                name.header = Header(Win32.DISPLAYCONFIG_DEVICE_INFO_GET_SOURCE_NAME, sizeof(Win32.DISPLAYCONFIG_SOURCE_DEVICE_NAME),
                    path.sourceInfo.adapterId, path.sourceInfo.id);
                if (Win32.DisplayConfigGetDeviceInfo(&name.header) != 0) continue;

                var color = new Win32.DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO();
                color.header = Header(Win32.DISPLAYCONFIG_DEVICE_INFO_GET_ADVANCED_COLOR_INFO, sizeof(Win32.DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO),
                    path.targetInfo.adapterId, path.targetInfo.id);
                bool advanced = Win32.DisplayConfigGetDeviceInfo(&color.header) == 0 && (color.value & 0x2) != 0;

                var white = new Win32.DISPLAYCONFIG_SDR_WHITE_LEVEL();
                white.header = Header(Win32.DISPLAYCONFIG_DEVICE_INFO_GET_SDR_WHITE_LEVEL, sizeof(Win32.DISPLAYCONFIG_SDR_WHITE_LEVEL),
                    path.targetInfo.adapterId, path.targetInfo.id);
                float nits = Win32.DisplayConfigGetDeviceInfo(&white.header) == 0 && white.SDRWhiteLevel > 0
                    ? white.SDRWhiteLevel * ScRgbWhiteNits / 1000f
                    : ScRgbWhiteNits;

                map[new string(name.viewGdiDeviceName)] = (advanced, nits);
            }
        }
        catch (Exception ex) when (ex is EntryPointNotFoundException or DllNotFoundException)
        {
            Log.Warn($"Display colour query unavailable: {ex.Message}");
        }

        return map;
    }

    private static Win32.DISPLAYCONFIG_DEVICE_INFO_HEADER Header(uint type, int size, Win32.LUID adapter, uint id) =>
        new() { type = type, size = (uint)size, adapterId = adapter, id = id };

    private static List<nint> MonitorHandles()
    {
        var handles = new List<nint>();
        var handle = GCHandle.Alloc(handles);
        try
        {
            Win32.EnumDisplayMonitors(0, 0, &OnMonitor, GCHandle.ToIntPtr(handle));
        }
        finally
        {
            handle.Free();
        }

        return handles;
    }

    [UnmanagedCallersOnly]
    private static int OnMonitor(nint monitor, nint hdc, Win32.RECT* rect, nint lParam)
    {
        ((List<nint>)GCHandle.FromIntPtr(lParam).Target!).Add(monitor);
        return 1;
    }
}
