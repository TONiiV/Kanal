using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Headless.XUnit;
using Avalonia.Styling;

namespace Kanal.UI.UnitTests;

public class ThemeResourceTests
{
    private static readonly string[] ColourKeys =
    [
        "Sheet", "Paper", "Ink", "Ink2", "Ink3", "Rule", "RuleFaint", "Alarm", "CloseHover",
        "Record", "Hold", "RecordWash", "HoldWash",
        "Brand", "BrandHover", "BrandPressed", "BrandWash", "BrandAccent", "OnBrand",
        "SpeakerFallbackColor", "TickMixedColor",
    ];

    [AvaloniaFact]
    public void EveryColourKeyLivesInTheLightThemeDictionary()
    {
        var app = Application.Current!;

        Assert.All(ColourKeys, key =>
            Assert.True(app.TryGetResource(key, ThemeVariant.Light, out var value) && value is not null,
                $"{key} is missing from the Light theme dictionary."));
    }

    [Fact]
    public void NoViewReadsAColourKeyThroughAStaticResource()
    {
        var root = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(root, "Kanal.slnx")))
            root = Path.GetDirectoryName(root) ?? throw new DirectoryNotFoundException("Kanal.slnx");

        var pattern = new Regex(@"\{StaticResource (" + string.Join("|", ColourKeys) + @")\}");
        var offenders = Directory.EnumerateFiles(Path.Combine(root, "src", "Kanal.Host"), "*.axaml", SearchOption.AllDirectories)
            .Where(file => pattern.IsMatch(File.ReadAllText(file)))
            .Select(Path.GetFileName);

        Assert.Empty(offenders);
    }
}
