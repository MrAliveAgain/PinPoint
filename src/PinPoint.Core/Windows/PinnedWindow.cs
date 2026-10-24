namespace PinPoint.Core.Windows;

/// <summary>An external window PinPoint has made topmost, plus what to restore on unpin.</summary>
public sealed class PinnedWindow
{
    internal PinnedWindow(nint handle, string title, string processName, bool wasTopmost, bool wasLayered)
    {
        Handle = handle;
        Title = title;
        ProcessName = processName;
        WasTopmost = wasTopmost;
        WasLayered = wasLayered;
    }

    public nint Handle { get; }

    public string Title { get; internal set; }

    public string ProcessName { get; }

    /// <summary>Opacity 0.1–1.0. Unavailable for windows that were already layered (they manage alpha themselves).</summary>
    public double Opacity { get; internal set; } = 1.0;

    public bool IsClickThrough { get; internal set; }

    public bool SupportsOpacity => !WasLayered;

    internal bool WasTopmost { get; }

    internal bool WasLayered { get; }

    internal bool WasTransparent { get; init; }

    internal bool IsHidden { get; set; }
}
