using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using PinPoint.Core.Geometry;
using PinPoint.Core.Interop;
using Windows.Graphics;

namespace PinPoint.Helpers;

public static class WindowHelpers
{
    public static string IconPath { get; } = Path.Combine(AppContext.BaseDirectory, "Assets", "PinPoint.ico");

    public static nint GetHandle(this Window window) => WinRT.Interop.WindowNative.GetWindowHandle(window);

    /// <summary>DPI scale of the monitor the window is on (1.0 = 100%).</summary>
    public static double GetScale(nint hwnd) => Win32.GetDpiForWindow(hwnd) / 96.0;

    /// <summary>
    /// Borderless, caption-less window that still gets Windows 11 rounded corners, border and shadow.
    /// Hidden from Alt+Tab and the taskbar.
    /// </summary>
    public static OverlappedPresenter MakeToolSurface(AppWindow appWindow, bool alwaysOnTop)
    {
        var presenter = OverlappedPresenter.Create();
        presenter.IsResizable = false;
        presenter.IsMaximizable = false;
        presenter.IsMinimizable = false;
        presenter.IsAlwaysOnTop = alwaysOnTop;
        presenter.PreferredMinimumWidth = 1;
        presenter.PreferredMinimumHeight = 1;
        presenter.SetBorderAndTitleBar(hasBorder: true, hasTitleBar: false);
        appWindow.SetPresenter(presenter);
        appWindow.IsShownInSwitchers = false;
        appWindow.SetIcon(IconPath);
        return presenter;
    }

    /// <summary>Client area in screen pixels.</summary>
    public static PixelRect GetClientScreenRect(nint hwnd, AppWindow appWindow)
    {
        var origin = new Win32.POINT();
        Win32.ClientToScreen(hwnd, ref origin);
        var size = appWindow.ClientSize;
        return new PixelRect(origin.X, origin.Y, size.Width, size.Height);
    }

    /// <summary>
    /// Positions the window so its client area lands exactly on <paramref name="client"/>.
    /// <paramref name="discardOldContent"/> stops Windows copying the old picture into the resized
    /// window (SWP_NOCOPYBITS), which otherwise shows as afterimages during rapid resizes.
    /// </summary>
    public static void SetClientScreenRect(nint hwnd, AppWindow appWindow, PixelRect client, bool discardOldContent = false)
    {
        var current = GetClientScreenRect(hwnd, appWindow);
        var pos = appWindow.Position;
        var size = appWindow.Size;

        int left = current.X - pos.X;
        int top = current.Y - pos.Y;
        int extraWidth = size.Width - current.Width;
        int extraHeight = size.Height - current.Height;

        var target = new RectInt32(client.X - left, client.Y - top, client.Width + extraWidth, client.Height + extraHeight);
        if (discardOldContent)
        {
            Win32.SetWindowPos(hwnd, 0, target.X, target.Y, target.Width, target.Height,
                Win32.SWP_NOZORDER | Win32.SWP_NOACTIVATE | Win32.SWP_NOOWNERZORDER | Win32.SWP_NOCOPYBITS);
        }
        else
        {
            appWindow.MoveAndResize(target);
        }
    }
}
