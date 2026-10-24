using System.Runtime.InteropServices;
using PinPoint.Core.Diagnostics;
using PinPoint.Core.Interop;

namespace PinPoint.Core.Windows;

/// <summary>
/// Raises <see cref="ForegroundChanged"/> whenever another app's window is activated.
/// Out-of-context WinEvent hook: no DLL injection, callbacks arrive on the creating (UI) thread.
/// </summary>
public sealed unsafe class ForegroundWatcher : IDisposable
{
    private static ForegroundWatcher? s_current;
    private nint _hook;

    public ForegroundWatcher()
    {
        s_current = this;
        _hook = Win32.SetWinEventHook(Win32.EVENT_SYSTEM_FOREGROUND, Win32.EVENT_SYSTEM_FOREGROUND, 0, &OnEvent, 0, 0,
            Win32.WINEVENT_OUTOFCONTEXT | Win32.WINEVENT_SKIPOWNPROCESS);
        if (_hook == 0) Log.Warn("SetWinEventHook failed; pins may be covered by topmost games.");
    }

    public event Action<nint>? ForegroundChanged;

    public void Dispose()
    {
        if (_hook != 0)
        {
            Win32.UnhookWinEvent(_hook);
            _hook = 0;
        }

        if (s_current == this) s_current = null;
    }

    [UnmanagedCallersOnly]
    private static void OnEvent(nint hook, uint eventType, nint hwnd, int idObject, int idChild, uint thread, uint time)
    {
        try
        {
            s_current?.ForegroundChanged?.Invoke(hwnd);
        }
        catch (Exception ex)
        {
            Log.Error("Foreground handler failed", ex);
        }
    }
}
