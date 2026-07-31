using Avalonia;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Styling;

namespace Kanal.Tests;

/// <summary>
/// ".impeccable.md — the only colour on screen is people": rust/ochre/pine identify a person,
/// and chrome is ink and paper. FluentTheme paints checked boxes, radios and selection in the
/// system accent, which puts a saturated blue next to the speaker hues and competes with them.
/// </summary>
public class ChromePaletteTests
{
    private static readonly string[] AccentKeys =
    [
        "SystemAccentColor",
        "SystemAccentColorLight1", "SystemAccentColorLight2", "SystemAccentColorLight3",
        "SystemAccentColorDark1", "SystemAccentColorDark2", "SystemAccentColorDark3",
    ];

    [AvaloniaFact]
    public void ChromeAccentsComeFromTheInkPalette()
    {
        var app = Application.Current!;

        var palette = new[] { "Ink", "Ink2", "Ink3", "Rule", "RuleFaint" }
            .Select(key =>
            {
                Assert.True(app.TryGetResource(key, ThemeVariant.Light, out var brush), key);
                return ((SolidColorBrush)brush!).Color;
            })
            .ToHashSet();

        foreach (var key in AccentKeys)
        {
            Assert.True(app.TryGetResource(key, ThemeVariant.Light, out var value), $"{key} unresolved");
            var color = Assert.IsType<Color>(value);
            Assert.True(
                palette.Contains(color),
                $"{key} is {color} — chrome accents must come from the ink palette, not the system accent.");
        }
    }
}
