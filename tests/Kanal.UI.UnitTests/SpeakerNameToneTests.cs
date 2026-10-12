using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Kanal.Host.Services;
using Kanal.Core.Room;
using Kanal.Host;
using Kanal.Host.ViewModels;
using Kanal.Host.Views;

namespace Kanal.UI.UnitTests;

public class SpeakerNameToneTests
{
    private static void Restore()
    {
        Appearance.SystemChanged(PlatformThemeVariant.Light);
        Appearance.Apply(ColourScheme.Light);
    }

    private static Color Parse(string hex) => Color.Parse(hex);

    private static Color Resource(ThemeVariant variant, string key)
    {
        Assert.True(Application.Current!.TryGetResource(key, variant, out var value), $"{key} is missing.");
        return value is ISolidColorBrush brush ? brush.Color : (Color)value!;
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

    private static IEnumerable<string> EveryNameColour()
    {
        foreach (var colour in RoomState.Palette)
            yield return colour;
        yield return "#" + Resource(ThemeVariant.Dark, "SpeakerFallbackColor").ToString()[3..];
    }

    [AvaloniaFact]
    public void EveryDarkNameReachesFourAndAHalfToOneOnTheDarkSurfaces()
    {
        foreach (var surface in new[] { "Sheet", "Paper" })
        {
            var background = Resource(ThemeVariant.Dark, surface);
            foreach (var colour in EveryNameColour())
            {
                var ratio = Contrast(Parse(SpeakerTone.Text(colour, SpeakerTone.DarkInk)), background);
                Assert.True(ratio >= 4.5, $"{colour} on dark {surface} is {ratio:F2}:1, below 4.5:1.");
            }
        }
    }

    [AvaloniaFact]
    public void EveryLightNameReachesFourAndAHalfToOneOnTheLightSurfaces()
    {
        foreach (var surface in new[] { "Sheet", "Paper" })
        {
            var background = Resource(ThemeVariant.Light, surface);
            foreach (var colour in RoomState.Palette)
            {
                var ratio = Contrast(Parse(SpeakerTone.Text(colour, SpeakerTone.LightInk)), background);
                Assert.True(ratio >= 4.5, $"{colour} on light {surface} is {ratio:F2}:1, below 4.5:1.");
            }
        }
    }

    private static (Window Window, MainViewModel Vm, string[] Colours) Shown()
    {
        var vm = TestViewModels.Hermetic();
        var column = new ColumnViewModel("de");
        vm.Columns.Add(column);
        var colours = RoomState.Palette.Take(3).ToArray();
        for (var i = 0; i < colours.Length; i++)
        {
            var bubble = column.GetOrAdd($"u{i}");
            bubble.SpeakerName = $"Sprecher{i}";
            bubble.SpeakerColor = colours[i];
            bubble.Text = "Hallo";
            vm.Speakers.Add(new SpeakerItemViewModel(_ => { }) { Tag = $"Tag{i}", Color = colours[i] });
        }

        var window = new MainWindow { DataContext = vm, Width = 1320, Height = 820 };
        window.Show();
        var tab = window.GetLogicalDescendants().OfType<TabItem>().Single(t => t.Name == "SpeakersTab");
        tab.IsVisible = true;
        tab.IsSelected = true;
        Settle(window);
        return (window, vm, colours);
    }

    private static void Settle(Window window)
    {
        for (var i = 0; i < 4; i++)
        {
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
        }
    }

    private static IEnumerable<IBrush?> Names(Window window, string prefix) =>
        window.GetVisualDescendants().OfType<TextBlock>()
            .Where(t => t.Text?.StartsWith(prefix, StringComparison.Ordinal) == true)
            .OrderBy(t => t.Text)
            .Select(t => t.Foreground);

    private static void AssertNames(Window window, MainViewModel vm, string[] speakerColours, string ink)
    {
        var bubbleColours = vm.Columns.Single().Bubbles.Select(b => b.SpeakerColor).ToArray();
        foreach (var (prefix, colours) in new[] { ("Sprecher", bubbleColours), ("Tag", speakerColours) })
        {
            var names = Names(window, prefix).Select(b => ((ISolidColorBrush)b!).Color).ToArray();
            Assert.Equal(colours.Select(c => Parse(SpeakerTone.Text(c, ink))), names);
        }
    }

    [AvaloniaFact]
    public void ANameFollowsTheSchemeAndRepaintsWhenItChanges()
    {
        try
        {
            var (window, vm, colours) = Shown();
            AssertNames(window, vm, colours, SpeakerTone.LightInk);

            Appearance.Apply(ColourScheme.Dark);
            Settle(window);
            AssertNames(window, vm, colours, SpeakerTone.DarkInk);

            Appearance.Apply(ColourScheme.Light);
            Settle(window);
            AssertNames(window, vm, colours, SpeakerTone.LightInk);
            window.Close();
        }
        finally
        {
            Restore();
        }
    }

    [AvaloniaFact]
    public void ANameRepaintsWhenSystemFollowsTheOperatingSystem()
    {
        try
        {
            var (window, vm, colours) = Shown();
            Appearance.Apply(ColourScheme.System);

            Appearance.SystemChanged(PlatformThemeVariant.Dark);
            Settle(window);
            AssertNames(window, vm, colours, SpeakerTone.DarkInk);

            Appearance.SystemChanged(PlatformThemeVariant.Light);
            Settle(window);
            AssertNames(window, vm, colours, SpeakerTone.LightInk);
            window.Close();
        }
        finally
        {
            Restore();
        }
    }
}
