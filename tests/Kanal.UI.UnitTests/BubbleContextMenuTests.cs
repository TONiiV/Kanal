using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Kanal.Core.Providers.Testing;
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

    private static void RequestContextAt(Window window, Control inside, double x, double y)
    {
        var at = inside.TranslatePoint(new Point(x, y), window)!.Value;
        var hit = Assert.IsAssignableFrom<Interactive>(window.InputHitTest(at));
        hit.RaiseEvent(new ContextRequestedEventArgs());
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
    public void AContextRequestOnTheBubblePaddingOpensTheMenuOnTheBubble()
    {
        var (window, translated, _) = Shown();
        var border = BubbleOf(window, translated);

        RequestContextAt(window, border, border.Bounds.Width / 2, border.Bounds.Height - 6);

        var flyout = Assert.IsType<MenuFlyout>(border.ContextFlyout);
        Assert.True(flyout.IsOpen);
        Assert.Same(border, flyout.Target);
        flyout.Hide();
        window.Close();
    }

    [AvaloniaFact]
    public void AContextRequestOnALineOpensTheMenuOnThatLineOnly()
    {
        var (window, translated, _) = Shown();
        var line = BubbleOf(window, translated).GetVisualDescendants().OfType<SelectableTextBlock>()
            .First(b => b.Text == Translation);

        RequestContextAt(window, line, 10, line.Bounds.Height / 2);

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

    [AvaloniaFact]
    public void PinFollowsTheBubbleTheMenuOpenedOnWhenItMovesBetweenBubbles()
    {
        var (window, translated, original) = Shown();
        translated.IsPartial = true;
        var first = LineOf(window, translated);
        var second = LineOf(window, original);

        var flyout = OpenMenuOn(window, first);
        Assert.False(Item(flyout, "PinToPoints").IsEffectivelyEnabled);
        flyout.Hide();
        Settle(window);

        OpenMenuOn(window, second);
        Assert.Same(original.PinCommand, Item(flyout, "PinToPoints").Command);
        Assert.True(Item(flyout, "PinToPoints").IsEffectivelyEnabled);
        flyout.Hide();
        Settle(window);

        OpenMenuOn(window, first);
        Assert.False(Item(flyout, "PinToPoints").IsEffectivelyEnabled);
        flyout.Hide();
        window.Close();
    }

    [AvaloniaFact]
    public void PinFollowsTheMenuStraightFromOneBubbleToAnother()
    {
        var (window, translated, original) = Shown();
        translated.IsPartial = true;
        var first = LineOf(window, translated);
        var second = LineOf(window, original);

        var flyout = OpenMenuOn(window, first);
        Assert.False(Item(flyout, "PinToPoints").IsEffectivelyEnabled);

        OpenMenuOn(window, second);

        Assert.Same(second, flyout.Target);
        Assert.Same(original.PinCommand, Item(flyout, "PinToPoints").Command);
        Assert.True(Item(flyout, "PinToPoints").IsEffectivelyEnabled);
        flyout.Hide();
        window.Close();
    }

    [AvaloniaFact]
    public async Task PinIsEnabledOnAFinalTranslatedBubbleOfALiveMeeting()
    {
        var vm = TestViewModels.Demo();
        vm.PlanFilter = plan => plan with
        {
            Asr = new FakeAsrProvider(
                script: [new FakeAsrProvider.Line("S01", "zh", "零件不合适。")], loop: true),
        };
        var window = new MainWindow { DataContext = vm, Width = 1320, Height = 820 };
        window.Show();
        await vm.StartCommand.ExecuteAsync(null);
        var deadline = Environment.TickCount64 + 15_000;
        while (!vm.Columns.All(c => c.Bubbles.Any(b => !b.IsPartial && !b.AwaitingTranslation)))
        {
            Assert.True(Environment.TickCount64 < deadline, "No final translated bubble in time.");
            Settle(window);
            await Task.Delay(50);
        }

        var translated = vm.Columns.First(c => c.Language != "zh").Bubbles.First(b => !b.IsPartial && !b.AwaitingTranslation);
        var flyout = OpenMenuOn(window, LineOf(window, translated));

        var pin = Item(flyout, "PinToPoints");
        Assert.True(translated.PinCommand.CanExecute(translated.Text));
        Assert.True(pin.IsEffectivelyEnabled, $"same={ReferenceEquals(pin.Command, translated.PinCommand)} param={pin.CommandParameter}");
        flyout.Hide();
        await vm.StopCommand.ExecuteAsync(null);
        window.Close();
    }
}
