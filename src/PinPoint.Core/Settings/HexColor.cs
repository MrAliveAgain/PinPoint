using System.Globalization;

namespace PinPoint.Core.Settings;

public static class HexColor
{
    /// <summary>Parses "#RRGGBB" or "RRGGBB".</summary>
    public static bool TryParse(string? text, out byte r, out byte g, out byte b)
    {
        r = g = b = 0;
        if (string.IsNullOrWhiteSpace(text)) return false;

        var span = text.AsSpan().Trim().TrimStart('#');
        if (span.Length != 6 || !uint.TryParse(span, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint value))
            return false;

        r = (byte)(value >> 16);
        g = (byte)(value >> 8);
        b = (byte)value;
        return true;
    }

    public static string Format(byte r, byte g, byte b) => $"#{r:X2}{g:X2}{b:X2}";
}
