using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Xaml.Media.Imaging;
using PinPoint.Core.Capture;

namespace PinPoint.Helpers;

public static class BitmapHelpers
{
    /// <summary>Copies a captured frame into a XAML bitmap (BGRA premultiplied, which an opaque frame already is).</summary>
    public static WriteableBitmap ToWriteableBitmap(this CapturedFrame frame)
    {
        var bitmap = new WriteableBitmap(frame.Width, frame.Height);
        using (var pixels = bitmap.PixelBuffer.AsStream())
        {
            pixels.Write(frame.Pixels);
        }

        bitmap.Invalidate();
        return bitmap;
    }
}
