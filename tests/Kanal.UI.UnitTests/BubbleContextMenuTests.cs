using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Kanal.Host.ViewModels;
using Kanal.Host.Views;

namespace Kanal.UI.UnitTests;

public class BubbleContextMenuTests
{
    private const string Translation = "Das Bauteil passt nicht.";
    private const string Source = "零件不合适。";

    private static void Settle(Window window)
    {
        for (var i = 0; i < 4; i++)
        {
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
        }
    }

    private static (Window Window, BubbleViewModel Translated, BubbleViewModel Original) Shown()
    {
        var vm = TestViewModels.Hermetic();
        vm.Columns.Add(new ColumnViewModel("de", null, (_, _) => { }));
        vm.Columns.Add(new ColumnViewModel("zh", null, (_, _) => { }));
        var translated = vm.Columns[0].GetOrAdd("u1");
        translated.SpeakerName = "Anna";
        translated.Text = Translation;
        translated.SourceText = Source;
        translated.IsPartial = false;
        var original = vm.Columns[1].GetOrAdd("u1");
        original.SpeakerName = "Anna";
        original.IsTranscript = true;
        original.Text = Source;
        original.IsPartial = false;

        var window = new MainWindow { DataContext = vm, Width = 1320, Height = 820 };
        window.Show();
        Settle(window);
        return (window, translated, original);
    }

    private static Border BubbleOf(Window window, BubbleViewModel bubble) =>
        window.GetVisualDescendants().OfType<Border>()
            .Single(b => b.Classes.Contains("rec") && ReferenceEquals(b.DataContext, bubble));

    private static SelectableTextBlock LineOf(Window window, BubbleViewModel bubble) =>
        BubbleOf(window, bubble).GetVisualDescendants().OfType<SelectableTextBlock>().First();

    private static MenuFlyout OpenMenuOn(Window window, Control target)
    {
        var flyout = Assert.IsType<MenuFlyout>(target.ContextFlyout);
        flyout.ShowAt(target);
        Settle(window);
        return flyout;
    }

    private static MenuItem Item(MenuFlyout flyout, string name) =>
        flyout.Items.OfType<MenuItem>().Single(i => i.Name == name);

    private static void RightClick(Window window, Control target, double fromBottom)
    {
        var at = target.TranslatePoint(new Point(target.Bounds.Width / 2, target.Bounds.Height - fromBottom), window)!.Value;
        window.MouseDown(at, MouseButton.Right);
        window.MouseUp(at, MouseButton.Right);
        Settle(window);
    }

    [AvaloniaFact]
    public void TheMenuOnTheBubbleGivesItsItemsTheTranslationLine()
    {
        var (window, translated, _) = Shown();
        var border = BubbleOf(window, translated);

        var flyout = OpenMenuOn(window, border);

        Assert.Equal(Translation, Item(flyout, "CopyLine").CommandParameter);
        Assert.Equal(Translation, Item(flyout, "PinToPoints").CommandParameter);
        flyout.Hide();
        window.Close();
    }

    [AvaloniaFact]
    public void TheMenuOnTheBubbleOfTheOriginalColumnGivesItsItemsTheSourceLine()
    {
        var (window, _, original) = Shown();
        var border = BubbleOf(window, original);

        var flyout = OpenMenuOn(window, border);

        Assert.Equal(Source, Item(flyout, "CopyLine").CommandParameter);
        flyout.Hide();
        window.Close();
    }

    [AvaloniaFact]
    public void ARightClickOnTheBubblePaddingOpensTheMenuOnTheBubble()
    {
        var (window, translated, _) = Shown();
        var border = BubbleOf(window, translated);

        RightClick(window, border, fromBottom: 6);

        var flyout = Assert.IsType<MenuFlyout>(border.ContextFlyout);
        Assert.True(flyout.IsOpen);
        Assert.Same(border, flyout.Target);
        flyout.Hide();
        window.Close();
    }

    [AvaloniaFact]
    public void ARightClickOnALineOpensTheMenuOnThatLineOnly()
    {
        var (window, translated, _) = Shown();
        var line = BubbleOf(window, translated).GetVisualDescendants().OfType<SelectableTextBlock>()
            .First(b => b.Text == Translation);

        RightClick(window, line, fromBottom: line.Bounds.Height / 2);

        var flyout = Assert.IsType<MenuFlyout>(line.ContextFlyout);
        Assert.True(flyout.IsOpen);
        Assert.Same(line, flyout.Target);
        Assert.Equal(Translation, Item(flyout, "CopyLine").CommandParameter);
        flyout.Hide();
        window.Close();
    }

    [AvaloniaFact]
    public void PinIsEnabledOnAFinalTranslatedBubble()
    {
        var (window, translated, _) = Shown();
        translated.AwaitingTranslation = false;

        var flyout = OpenMenuOn(window, LineOf(window, translated));

        var pin = Item(flyout, "PinToPoints");
        Assert.True(translated.PinCommand.CanExecute(Translation));
        Assert.True(pin.IsEffectivelyEnabled, $"command={pin.Command?.GetHashCode()} bubble={translated.PinCommand.GetHashCode()} param={pin.CommandParameter}");
        flyout.Hide();
        window.Close();
    }

    [AvaloniaFact]
    public void PinBecomesEnabledWhenTheBubbleTurnsFinalBetweenTwoOpenings()
    {
        var (window, translated, _) = Shown();
        translated.IsPartial = true;
        translated.AwaitingTranslation = true;
        var border = LineOf(window, translated);
        var flyout = OpenMenuOn(window, border);
        Assert.False(Item(flyout, "PinToPoints").IsEffectivelyEnabled);
        flyout.Hide();
        Settle(window);

        translated.IsPartial = false;
        translated.AwaitingTranslation = false;
        OpenMenuOn(window, border);

        Assert.True(Item(flyout, "PinToPoints").IsEffectivelyEnabled);
        flyout.Hide();
        window.Close();
    }
}
