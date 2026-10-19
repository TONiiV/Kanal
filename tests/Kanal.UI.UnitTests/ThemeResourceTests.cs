using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Styling;

namespace Kanal.UI.UnitTests;

public class ThemeResourceTests
{
    private static string[] LightKeys()
    {
        var light = (ResourceDictionary)Application.Current!.Resources.ThemeDictionaries[ThemeVariant.Light];
        return light.Keys.Cast<string>().ToArray();
    }

    [AvaloniaFact]
    public void EveryColourKeyLivesOnlyInTheLightThemeDictionary()
    {
        var app = Application.Current!;
        var keys = LightKeys();

        Assert.NotEmpty(keys);
        Assert.All(keys, key =>
        {
            Assert.False(app.Resources.ContainsKey(key), $"{key} is also in the root dictionary.");
            Assert.True(app.TryGetResource(key, ThemeVariant.Light, out _), $"{key} does not resolve for Light.");
        });
    }

    [AvaloniaFact]
    public void NoViewReadsAColourKeyThroughAStaticResource()
    {
        var root = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(root, "Kanal.slnx")))
            root = Path.GetDirectoryName(root) ?? throw new DirectoryNotFoundException("Kanal.slnx");

        // FluentTheme ignores SystemAccentColor* in a theme dictionary, so these stay at the root.
        var accent = Application.Current!.Resources.Keys.Cast<string>()
            .Where(key => key.StartsWith("SystemAccentColor", StringComparison.Ordinal));
        var keys = string.Join("|", LightKeys().Concat(accent).Select(Regex.Escape));
        var pattern = new Regex(
            @"\{StaticResource\s+(?:ResourceKey=)?(?:" + keys + @")\s*\}" +
            @"|<StaticResource\s[^>]*ResourceKey=""(?:" + keys + @")""");
        var offenders = Directory.EnumerateFiles(Path.Combine(root, "src", "Kanal.Host"), "*.axaml", SearchOption.AllDirectories)
            .Where(file => pattern.IsMatch(File.ReadAllText(file)))
            .Select(Path.GetFileName);

        Assert.Empty(offenders);
    }
}
