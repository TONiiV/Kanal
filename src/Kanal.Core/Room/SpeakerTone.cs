using System.Globalization;

namespace Kanal.Core.Room;

public static class SpeakerTone
{
    public const string LightInk = "#252B3B";
    public const string DarkInk = "#F6F1E8";

    // The mobile page uses the same number in CSS color-mix(in oklab, ...). Change both together.
    public const int InkPercent = 30;

    public static string Text(string hex, string ink) => Mix(hex, ink, InkPercent);

    public static string Mix(string hex, string ink, int percent)
    {
        var a = ToOklab(hex);
        var b = ToOklab(ink);
        var t = percent / 100.0;
        return FromOklab(a[0] + (b[0] - a[0]) * t, a[1] + (b[1] - a[1]) * t, a[2] + (b[2] - a[2]) * t);
    }

    private static double[] ToOklab(string hex)
    {
        var rgb = int.Parse(hex.AsSpan(1), NumberStyles.HexNumber);
        var r = ToLinear(rgb >> 16 & 0xFF);
        var g = ToLinear(rgb >> 8 & 0xFF);
        var b = ToLinear(rgb & 0xFF);
        var l = Math.Cbrt(0.4122214708 * r + 0.5363325363 * g + 0.0514459929 * b);
        var m = Math.Cbrt(0.2119034982 * r + 0.6806995451 * g + 0.1073969566 * b);
        var s = Math.Cbrt(0.0883024619 * r + 0.2817188376 * g + 0.6299787005 * b);
        return
        [
            0.2104542553 * l + 0.793617785 * m - 0.0040720468 * s,
            1.9779984951 * l - 2.428592205 * m + 0.4505937099 * s,
            0.0259040371 * l + 0.7827717662 * m - 0.808675766 * s,
        ];
    }

    private static string FromOklab(double lightness, double a, double b)
    {
        var l = Math.Pow(lightness + 0.3963377774 * a + 0.2158037573 * b, 3);
        var m = Math.Pow(lightness - 0.1055613458 * a - 0.0638541728 * b, 3);
        var s = Math.Pow(lightness - 0.0894841775 * a - 1.291485548 * b, 3);
        var r = FromLinear(4.0767416621 * l - 3.3077115913 * m + 0.2309699292 * s);
        var g = FromLinear(-1.2684380046 * l + 2.6097574011 * m - 0.3413193965 * s);
        var bl = FromLinear(-0.0041960863 * l - 0.7034186147 * m + 1.707614701 * s);
        return $"#{r:X2}{g:X2}{bl:X2}";
    }

    private static double ToLinear(int channel)
    {
        var c = channel / 255.0;
        return c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
    }

    private static int FromLinear(double linear)
    {
        var c = Math.Clamp(linear, 0, 1);
        var encoded = c <= 0.0031308 ? 12.92 * c : 1.055 * Math.Pow(c, 1 / 2.4) - 0.055;
        return (int)Math.Round(encoded * 255);
    }
}
