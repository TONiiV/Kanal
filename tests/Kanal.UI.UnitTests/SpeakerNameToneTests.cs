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
using Kanal.Core.Models;
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

    private static IEnumerable<string> EveryNameColour()
    {
        foreach (var colour in RoomState.Palette)
            yield return colour;
        yield return "#" + ColourSchemeTests.Brush(ThemeVariant.Dark, "SpeakerFallbackColor").ToString()[3..];
    }

    [AvaloniaFact]
    public void EveryDarkNameReachesFourAndAHalfToOneOnTheDarkSurfaces()
    {
        foreach (var surface in new[] { "Sheet", "Paper" })
        {
            var background = ColourSchemeTests.Brush(ThemeVariant.Dark, surface);
            foreach (var colour in EveryNameColour())
            {
                var ratio = ColourSchemeTests.Contrast(Parse(SpeakerTone.Text(colour, SpeakerTone.DarkInk)), background);
                Assert.True(ratio >= 4.5, $"{colour} on dark {surface} is {ratio:F2}:1, below 4.5:1.");
            }
        }
    }

    [AvaloniaFact]
    public void EveryLightNameReachesFourAndAHalfToOneOnTheLightSurfaces()
    {
        foreach (var surface in new[] { "Sheet", "Paper" })
        {
            var background = ColourSchemeTests.Brush(ThemeVariant.Light, surface);
            foreach (var colour in RoomState.Palette)
            {
                var ratio = ColourSchemeTests.Contrast(Parse(SpeakerTone.Text(colour, SpeakerTone.LightInk)), background);
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
            vm.ApplySpeaker(new Speaker($"Tag{i}", $"Sprecher{i}", [], colours[i]));
            var bubble = column.GetOrAdd($"u{i}");
            bubble.SpeakerTag = $"Tag{i}";
            bubble.SpeakerName = $"Sprecher{i}";
            bubble.SpeakerColor = colours[i];
            bubble.Text = "Hallo";
        }

        // No speaker model: this bubble takes the theme fallback colour.
        var unknown = column.GetOrAdd("u9");
        unknown.SpeakerTag = "Sprecher9";
        unknown.SpeakerName = "Sprecher9";
        unknown.SpeakerColor = ThemeColours.SpeakerFallback;
        unknown.Text = "Hallo";

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
        Assert.Equal(speakerColours, bubbleColours.Take(speakerColours.Length));
        foreach (var (prefix, colours) in new[] { ("Sprecher", bubbleColours), ("Tag", speakerColours) })
        {
            var names = Names(window, prefix).Select(b => ((ISolidColorBrush)b!).Color).ToArray();
            Assert.Equal(colours.Select(c => Parse(SpeakerTone.Text(c, ink))), names);
        }
    }

    [AvaloniaFact]
    public void ANameFollowsTheSchemeAndRepaintsWhenItChanges()
    {
        Window? window = null;
        try
        {
            (window, var vm, var colours) = Shown();
            AssertNames(window, vm, colours, SpeakerTone.LightInk);

            Appearance.Apply(ColourScheme.Dark);
            Settle(window);
            AssertNames(window, vm, colours, SpeakerTone.DarkInk);

            Appearance.Apply(ColourScheme.Light);
            Settle(window);
            AssertNames(window, vm, colours, SpeakerTone.LightInk);
        }
        finally
        {
            window?.Close();
            Restore();
        }
    }

    [AvaloniaFact]
    public void ANameRepaintsWhenSystemFollowsTheOperatingSystem()
    {
        Window? window = null;
        try
        {
            (window, var vm, var colours) = Shown();
            Appearance.Apply(ColourScheme.System);

            Appearance.SystemChanged(PlatformThemeVariant.Dark);
            Settle(window);
            AssertNames(window, vm, colours, SpeakerTone.DarkInk);

            Appearance.SystemChanged(PlatformThemeVariant.Light);
            Settle(window);
            AssertNames(window, vm, colours, SpeakerTone.LightInk);
        }
        finally
        {
            window?.Close();
            Restore();
        }
    }
}
