using Kanal.Core.Room;

namespace Kanal.Core.UnitTests;

public class SpeakerPaletteTests
{
    private const string LightSheet = "#FFFCF7";
    private const string DarkSheet = "#181C28";
    private const double MinimumContrast = 3.0;

    public static TheoryData<string> Colours()
    {
        var data = new TheoryData<string>();
        foreach (var colour in RoomState.Palette)
            data.Add(colour);
        return data;
    }

    [Fact]
    public void PaletteHoldsEightDistinctColours()
    {
        Assert.Equal(8, RoomState.Palette.Count);
        Assert.Equal(8, RoomState.Palette.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Theory]
    [MemberData(nameof(Colours))]
    public void ColourReachesThreeToOneOnBothSheets(string colour)
    {
        Assert.True(Contrast(colour, LightSheet) >= MinimumContrast, $"{colour} on light sheet");
        Assert.True(Contrast(colour, DarkSheet) >= MinimumContrast, $"{colour} on dark sheet");
    }

    private static double Contrast(string a, string b)
    {
        var (hi, lo) = (Luminance(a), Luminance(b));
        if (hi < lo)
            (hi, lo) = (lo, hi);
        return (hi + 0.05) / (lo + 0.05);
    }

    private static double Luminance(string hex)
    {
        double Channel(int shift)
        {
            var c = int.Parse(hex.AsSpan(1), System.Globalization.NumberStyles.HexNumber) >> shift & 0xFF;
            var s = c / 255.0;
            return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
        }

        return 0.2126 * Channel(16) + 0.7152 * Channel(8) + 0.0722 * Channel(0);
    }
}
