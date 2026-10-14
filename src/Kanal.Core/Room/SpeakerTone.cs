using System.Globalization;

namespace Kanal.Core.Room;

public static class SpeakerTone
{
    public const string LightInk = "#252B3B";
    public const string DarkInk = "#F6F1E8";

    // The mobile page uses the same number in CSS color-mix(in srgb, ...). Change both together.
    public const int InkPercent = 30;

    public static string Text(string hex, string ink) => Mix(hex, ink, InkPercent);

    public static string Mix(string hex, string ink, int percent)
    {
        var a = int.Parse(hex.AsSpan(1), NumberStyles.HexNumber);
        var b = int.Parse(ink.AsSpan(1), NumberStyles.HexNumber);
        int Channel(int shift) =>
            (int)Math.Round((a >> shift & 0xFF) + ((b >> shift & 0xFF) - (a >> shift & 0xFF)) * percent / 100.0);
        return $"#{Channel(16):X2}{Channel(8):X2}{Channel(0):X2}";
    }
}
