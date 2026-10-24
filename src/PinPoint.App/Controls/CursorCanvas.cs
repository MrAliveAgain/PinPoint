using Microsoft.UI.Input;
using Microsoft.UI.Xaml.Controls;

namespace PinPoint.Controls;

/// <summary>A Canvas whose mouse cursor can be set from outside (WinUI only exposes it to subclasses).</summary>
public sealed partial class CursorCanvas : Canvas
{
    public InputCursor? Cursor
    {
        get => ProtectedCursor;
        set => ProtectedCursor = value;
    }
}
