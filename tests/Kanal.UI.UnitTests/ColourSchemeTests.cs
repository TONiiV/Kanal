using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Styling;
using Kanal.Core.Models;
using Kanal.Host;
using Kanal.Host.Services;
using Kanal.Host.ViewModels;

namespace Kanal.UI.UnitTests;

public class ColourSchemeTests
{
    private static void Restore()
    {
        Appearance.SystemChanged(PlatformThemeVariant.Light);
        Appearance.Apply(ColourScheme.Light);
    }

    private static ColourSchemeOption Option(SettingsViewModel vm, ColourScheme scheme) =>
        vm.Schemes.Single(option => option.Scheme == scheme);

    [AvaloniaFact]
    public void ANewMachineStartsLight()
    {
        Assert.Equal(ColourScheme.Light, new AppSettings().ColourScheme);
        Assert.Equal(ColourScheme.Light, new SettingsViewModel(new AppSettings(), () => null).Scheme.Scheme);
    }

    [AvaloniaFact]
    public void ChoosingASchemeAppliesItAtOnceAndSavesIt()
    {
        try
        {
            var saved = new List<ColourScheme>();
            var vm = new SettingsViewModel(new AppSettings(), () => null, saveColourScheme: saved.Add);

            vm.Scheme = Option(vm, ColourScheme.Dark);

            Assert.Equal(ThemeVariant.Dark, Application.Current!.ActualThemeVariant);
            Assert.Equal([ColourScheme.Dark], saved);
            var written = new AppSettings();
            vm.ApplyTo(written);
            Assert.Equal(ColourScheme.Dark, written.ColourScheme);

            vm.Scheme = Option(vm, ColourScheme.Light);

            Assert.Equal(ThemeVariant.Light, Application.Current.ActualThemeVariant);
        }
        finally
        {
            Restore();
        }
    }

    [AvaloniaFact]
    public void SystemFollowsTheOperatingSystemWhileItRuns()
    {
        try
        {
            var vm = new SettingsViewModel(new AppSettings(), () => null, saveColourScheme: _ => { });

            vm.Scheme = Option(vm, ColourScheme.System);
            Assert.Equal(ThemeVariant.Light, Application.Current!.ActualThemeVariant);

            Appearance.SystemChanged(PlatformThemeVariant.Dark);
            Assert.Equal(ThemeVariant.Dark, Application.Current.ActualThemeVariant);

            Appearance.SystemChanged(PlatformThemeVariant.Light);
            Assert.Equal(ThemeVariant.Light, Application.Current.ActualThemeVariant);
        }
        finally
        {
            Restore();
        }
    }

    [AvaloniaFact]
    public void AnExplicitSchemeIgnoresTheOperatingSystem()
    {
        try
        {
            Appearance.Apply(ColourScheme.Dark);

            Appearance.SystemChanged(PlatformThemeVariant.Light);

            Assert.Equal(ThemeVariant.Dark, Application.Current!.ActualThemeVariant);
        }
        finally
        {
            Restore();
        }
    }

    [AvaloniaFact]
    public void TheFluentAccentRampFollowsTheVariant()
    {
        try
        {
            var root = Application.Current!.Resources;
            var ramp = root.Keys.Cast<string>()
                .Where(key => key.StartsWith("SystemAccentColor", StringComparison.Ordinal))
                .ToList();
            Assert.Equal(7, ramp.Count);

            foreach (var (scheme, variant) in new[] { (ColourScheme.Dark, ThemeVariant.Dark), (ColourScheme.Light, ThemeVariant.Light) })
            {
                Appearance.Apply(scheme);
                Assert.All(ramp, key => Assert.Equal(
                    Brush(variant, "AccentRamp" + key["SystemAccentColor".Length..]), (Color)root[key]!));
                // FluentTheme paints a checked box and the settings tab marker with the base colour.
                Assert.Equal(Brush(variant, "Brand"), (Color)root["SystemAccentColor"]!);
            }
        }
        finally
        {
            Restore();
        }
    }

    [AvaloniaFact]
    public void ASchemeChangeRepaintsTheRulerMarksAlreadyDrawn()
    {
        try
        {
            var vm = TestViewModels.Hermetic();
            for (var i = 0; i < 200; i++)
            {
                vm.Ruler.Observe(new Utterance($"u{i}", i % 2 == 0 ? "s1" : "s2", 0, 900, "zh", $"turn {i}", 1,
                    UtteranceState.Final, false, 1.0, new Dictionary<string, string>()));
            }

            Appearance.Apply(ColourScheme.Dark);

            var mixed = Brush(ThemeVariant.Dark, "TickMixedColor");
            Assert.All(vm.Ruler.Ticks, tick => Assert.Equal($"#{mixed.R:X2}{mixed.G:X2}{mixed.B:X2}", tick.SpeakerColor));
        }
        finally
        {
            Restore();
        }
    }

    [AvaloniaFact]
    public void ASettingsFileWithAnUnknownSchemeFallsBackToLight()
    {
        var settings = System.Text.Json.JsonSerializer.Deserialize<AppSettings>("""{"ColourScheme":"Sepia"}""")!;

        Assert.Equal(ColourScheme.Light, settings.ColourScheme);
    }

    private static Color Brush(ThemeVariant variant, string key)
    {
        Assert.True(Application.Current!.TryGetResource(key, variant, out var value), $"{key} is missing for {variant}.");
        return value switch
        {
            ISolidColorBrush brush => brush.Color,
            Color colour => colour,
            _ => throw new InvalidOperationException($"{key} is not a colour."),
        };
    }

    private static double Luminance(Color c)
    {
        static double Channel(byte v)
        {
            var s = v / 255.0;
            return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
        }

        return 0.2126 * Channel(c.R) + 0.7152 * Channel(c.G) + 0.0722 * Channel(c.B);
    }

    private static double Contrast(Color a, Color b)
    {
        var (hi, lo) = (Math.Max(Luminance(a), Luminance(b)), Math.Min(Luminance(a), Luminance(b)));
        return (hi + 0.05) / (lo + 0.05);
    }

    public static TheoryData<string, string, string, double> Pairs()
    {
        var data = new TheoryData<string, string, string, double>();
        foreach (var variant in new[] { "Light", "Dark" })
        {
            foreach (var surface in new[] { "Sheet", "Paper" })
            {
                data.Add(variant, "Ink", surface, 4.5);
                data.Add(variant, "Ink2", surface, 4.5);
                data.Add(variant, "Ink3", surface, 4.5);
                data.Add(variant, "Alarm", surface, 4.5);
                data.Add(variant, "SpeakerFallbackColor", surface, 4.5);
            }

            data.Add(variant, "Ink", "BrandWash", 4.5);
            data.Add(variant, "Brand", "Sheet", 4.5);
            data.Add(variant, "Brand", "BrandWash", 4.5);
            data.Add(variant, "OnBrand", "Brand", 4.5);
            data.Add(variant, "OnBrand", "BrandHover", 4.5);
            data.Add(variant, "OnBrand", "Alarm", 4.5);
            data.Add(variant, "OnCloseHover", "CloseHover", 4.5);
        }

        return data;
    }

    [AvaloniaTheory]
    [MemberData(nameof(Pairs))]
    public void TextStaysReadableInEveryVariant(string variant, string text, string surface, double minimum)
    {
        var theme = variant == "Dark" ? ThemeVariant.Dark : ThemeVariant.Light;

        var ratio = Contrast(Brush(theme, text), Brush(theme, surface));

        Assert.True(ratio >= minimum, $"{variant}: {text} on {surface} is {ratio:F2}:1, below {minimum}:1.");
    }

    [AvaloniaFact]
    public void EveryLightColourKeyHasADarkValue()
    {
        var dictionaries = Application.Current!.Resources.ThemeDictionaries;
        var light = (ResourceDictionary)dictionaries[ThemeVariant.Light];
        var dark = Assert.IsType<ResourceDictionary>(dictionaries[ThemeVariant.Dark]);

        Assert.Empty(light.Keys.Except(dark.Keys));
        Assert.Empty(dark.Keys.Except(light.Keys));
    }
}
