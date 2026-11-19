using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls.Primitives;
using PinPoint.Core.Interop;
using PinPoint.Core.Windows;

namespace PinPoint.Helpers;

/// <summary>
/// Opens a menu on a pin that normally never takes focus (snips, quick slots, timers, the click-through badge).
/// Windows closes a menu when its window loses focus; a window that never had focus can't lose it, so menus used
/// to stay open until you clicked PinPoint itself, and fought games for the top. While the menu is open its window
/// takes focus (so clicking anywhere else, desktop, another app, the game, closes it, and it sits solidly on top);
/// when it closes, focus goes back to whatever you were in, and the window goes back to never taking focus.
/// </summary>
public static class FlyoutFocus
{
    public static void ShowAt(Window window, FlyoutBase flyout, FrameworkElement target, FlyoutShowOptions options)
    {
        nint hwnd = window.GetHandle();
        nint previous = Win32.GetForegroundWindow();
        bool noActivate = WindowStyler.HasExStyle(hwnd, Win32.WS_EX_NOACTIVATE);
        if (noActivate) WindowStyler.SetExStyleFlag(hwnd, Win32.WS_EX_NOACTIVATE, false);
        Win32.ForceForeground(hwnd);

        void OnActivated(object sender, WindowActivatedEventArgs e)
        {
            if (e.WindowActivationState != WindowActivationState.Deactivated) return;
            // The menu's own popup belongs to us; only focus going to another app (or the desktop) closes it.
            window.DispatcherQueue.TryEnqueue(() =>
            {
                nint now = Win32.GetForegroundWindow();
                if (now == 0 || Win32.GetProcessId(now) != Environment.ProcessId) flyout.Hide();
            });
        }

        void OnClosed(object? sender, object e)
        {
            flyout.Closed -= OnClosed;
            window.Activated -= OnActivated;
            if (noActivate) WindowStyler.SetExStyleFlag(hwnd, Win32.WS_EX_NOACTIVATE, true);

            // Back to what you were in (the game), unless you've clicked somewhere else since.
            if (Win32.GetForegroundWindow() == hwnd && previous != 0 && previous != hwnd && Win32.IsWindow(previous))
                Win32.ForceForeground(previous);
        }

        flyout.Closed += OnClosed;
        window.Activated += OnActivated;
        flyout.ShowAt(target, options);
    }
}
