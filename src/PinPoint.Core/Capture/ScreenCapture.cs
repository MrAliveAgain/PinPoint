using System.Numerics;
using System.Runtime.InteropServices;
using PinPoint.Core.Geometry;
using PinPoint.Core.Interop;

namespace PinPoint.Core.Capture;

/// <summary>Grabs pixels from the screen. Swap implementations (e.g. Windows.Graphics.Capture) behind this.</summary>
public interface IScreenCapture
{
    CapturedFrame Capture(PixelRect area);
}

/// <summary>
/// GDI BitBlt of the composed desktop. ~10–30 ms for a full ultrawide, works for borderless/windowed
/// games and everything DWM composes. Exclusive-fullscreen games are not supported by any overlay anyway.
/// </summary>
public sealed unsafe class GdiScreenCapture : IScreenCapture
{
    /// <summary>Bounding box of all monitors, in physical pixels.</summary>
    public static PixelRect VirtualScreen => new(
        Win32.GetSystemMetrics(Win32.SM_XVIRTUALSCREEN),
        Win32.GetSystemMetrics(Win32.SM_YVIRTUALSCREEN),
        Win32.GetSystemMetrics(Win32.SM_CXVIRTUALSCREEN),
        Win32.GetSystemMetrics(Win32.SM_CYVIRTUALSCREEN));

    public CapturedFrame Capture(PixelRect area)
    {
        if (area.IsEmpty) throw new ArgumentException("Capture area must not be empty.", nameof(area));

        nint screenDc = Win32.GetDC(0);
        nint memoryDc = 0, bitmap = 0, previous = 0;
        try
        {
            memoryDc = Win32.CreateCompatibleDC(screenDc);
            var header = new Win32.BITMAPINFOHEADER
            {
                biSize = (uint)sizeof(Win32.BITMAPINFOHEADER),
                biWidth = area.Width,
                biHeight = -area.Height, // negative = top-down rows
                biPlanes = 1,
                biBitCount = 32,
            };

            bitmap = Win32.CreateDIBSection(screenDc, in header, 0, out nint bits, 0, 0);
            if (bitmap == 0 || bits == 0) throw new InvalidOperationException("CreateDIBSection failed.");

            previous = Win32.SelectObject(memoryDc, bitmap);
            if (!Win32.BitBlt(memoryDc, 0, 0, area.Width, area.Height, screenDc, area.X, area.Y, Win32.SRCCOPY | Win32.CAPTUREBLT))
                throw new InvalidOperationException($"BitBlt failed (error {Marshal.GetLastPInvokeError()}).");
            Win32.GdiFlush();

            int length = area.Width * area.Height * 4;
            var pixels = GC.AllocateUninitializedArray<byte>(length);
            new ReadOnlySpan<byte>((void*)bits, length).CopyTo(pixels);
            MakeOpaque(pixels);

            return new CapturedFrame(area, pixels);
        }
        finally
        {
            if (previous != 0) Win32.SelectObject(memoryDc, previous);
            if (bitmap != 0) Win32.DeleteObject(bitmap);
            if (memoryDc != 0) Win32.DeleteDC(memoryDc);
            Win32.ReleaseDC(0, screenDc);
        }
    }

    /// <summary>GDI leaves alpha at 0; XAML treats BGRA as premultiplied, so force 255.</summary>
    internal static void MakeOpaque(Span<byte> bgra)
    {
        var pixels = MemoryMarshal.Cast<byte, uint>(bgra);
        var mask = new Vector<uint>(0xFF000000u);
        int i = 0;
        for (; i <= pixels.Length - Vector<uint>.Count; i += Vector<uint>.Count)
        {
            var slice = pixels.Slice(i, Vector<uint>.Count);
            (new Vector<uint>(slice) | mask).CopyTo(slice);
        }

        for (; i < pixels.Length; i++) pixels[i] |= 0xFF000000u;
    }
}
