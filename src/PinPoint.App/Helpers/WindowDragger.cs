using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using PinPoint.Core.Interop;
using Windows.Graphics;

namespace PinPoint.Helpers;

/// <summary>
/// Moves a borderless window by dragging an element inside it. Tracks the cursor in screen space,
/// because pointer positions relative to a window that is itself moving would feed back on themselves.
/// </summary>
public sealed class WindowDragger(AppWindow window)
{
    private Win32.POINT _cursorStart;
    private PointInt32 _windowStart;

    public bool IsDragging { get; private set; }

    public void Begin(UIElement handle, Pointer pointer)
    {
        Win32.GetCursorPos(out _cursorStart);
        _windowStart = window.Position;
        IsDragging = handle.CapturePointer(pointer);
    }

    public void Update()
    {
        if (!IsDragging) return;
        Win32.GetCursorPos(out var cursor);
        window.Move(new PointInt32(_windowStart.X + cursor.X - _cursorStart.X, _windowStart.Y + cursor.Y - _cursorStart.Y));
    }

    /// <summary>Returns true if a drag was in progress.</summary>
    public bool End(UIElement handle, Pointer pointer)
    {
        if (!IsDragging) return false;
        IsDragging = false;
        handle.ReleasePointerCapture(pointer);
        return true;
    }

    public void Cancel() => IsDragging = false;
}
