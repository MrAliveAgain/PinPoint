using System.Runtime.InteropServices;

namespace PinPoint.Core.Interop;

public static unsafe partial class Win32
{
    /// <summary>The system "Exclamation" sound.</summary>
    public const uint MB_ICONEXCLAMATION = 0x00000030;

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool MessageBeep(uint uType);
}
