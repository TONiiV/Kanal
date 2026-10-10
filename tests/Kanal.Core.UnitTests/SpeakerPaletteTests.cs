using System.Globalization;
using Kanal.Core.Room;

namespace Kanal.Core.UnitTests;

public class SpeakerPaletteTests
{
    private const string LightSheet = "#FFFCF7";
    private const string LightPaper = "#F5F0E6";
    private const string DarkSheet = "#181C28";
    private const string DarkPaper = "#222838";
    private const double GraphicsContrast = 3.0;
    private const double TextContrast = 4.5;

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
    public void GraphicsColourReachesThreeToOneOnEverySurface(string colour)
    {
        foreach (var surface in new[] { LightSheet, LightPaper, DarkSheet, DarkPaper })
            Assert.True(Contrast(colour, surface) >= GraphicsContrast, $"{colour} on {surface}");
    }

    [Theory]
    [MemberData(nameof(Colours))]
    public void TextToneReachesFourPointFiveToOneOnItsOwnSurfaces(string colour)
    {
        var onLight = SpeakerTone.Text(colour, SpeakerTone.LightInk);
        var onDark = SpeakerTone.Text(colour, SpeakerTone.DarkInk);

        foreach (var surface in new[] { LightSheet, LightPaper })
            Assert.True(Contrast(onLight, surface) >= TextContrast, $"{onLight} on {surface}");
        foreach (var surface in new[] { DarkSheet, DarkPaper })
            Assert.True(Contrast(onDark, surface) >= TextContrast, $"{onDark} on {surface}");
    }

    [Fact]
    public void TextToneKeepsTheColourWhenNoInkIsMixedIn()
    {
        Assert.Equal("#3F7BD4", SpeakerTone.Mix("#3F7BD4", SpeakerTone.LightInk, 0));
        Assert.Equal(SpeakerTone.LightInk, SpeakerTone.Mix("#3F7BD4", SpeakerTone.LightInk, 100));
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
            var c = int.Parse(hex.AsSpan(1), NumberStyles.HexNumber) >> shift & 0xFF;
            var s = c / 255.0;
            return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
        }

        return 0.2126 * Channel(16) + 0.7152 * Channel(8) + 0.0722 * Channel(0);
    }
}
