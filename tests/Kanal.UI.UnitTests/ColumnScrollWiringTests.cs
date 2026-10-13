using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Kanal.Core.Models;
using Kanal.Host.ViewModels;
using Kanal.Host.Views;

namespace Kanal.UI.UnitTests;

public class ColumnScrollWiringTests
{
    private static void Say(MainViewModel vm, int n)
    {
        foreach (var column in vm.Columns)
        {
            var bubble = column.GetOrAdd($"u{n}");
            bubble.Text = column.Language == "de"
                ? $"Eine deutlich längere Zeile, die im schmalen Spaltenraum mehrfach umbrechen muss, Nummer {n}"
                : $"Zeile {n}";
        }
    }

    private static void Settle(Window window)
    {
        for (var i = 0; i < 4; i++)
        {
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
        }
    }

    private static (Window Window, MainViewModel Vm, MeetingRoomView Room, ScrollViewer[] Scrollers) Shown()
    {
        var vm = TestViewModels.Hermetic();
        foreach (var code in new[] { "zh", "de", "pl" })
            vm.Columns.Add(new ColumnViewModel(code));
        for (var n = 0; n < 40; n++)
            Say(vm, n);

        var window = new MainWindow { DataContext = vm, Width = 1320, Height = 820 };
        window.Show();
        Settle(window);
        var room = window.GetLogicalDescendants().OfType<MeetingRoomView>().Single();
        var scrollers = room.GetVisualDescendants().OfType<ScrollViewer>()
            .Where(s => s.Content is ItemsControl list && list.Classes.Contains("bubbles"))
            .ToArray();
        return (window, vm, room, scrollers);
    }

    private static Button Jump(MeetingRoomView room) =>
        room.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "JumpToLatest");

    [AvaloniaFact]
    public void TheColumnsOfTheRoomScrollTogether()
    {
        var (window, _, _, scrollers) = Shown();
        Assert.Equal(3, scrollers.Length);

        scrollers[1].Offset = new Vector(0, 200);
        Settle(window);

        Assert.All(scrollers, s => Assert.True(s.Offset.Y > 0, "a column stayed behind"));
        Assert.True(scrollers[1].Offset.Y > scrollers[0].Offset.Y, "the tall German lines should scroll further");
        window.Close();
    }

    [AvaloniaFact]
    public void NewContentBelowAReaderRaisesTheRoundJumpButtonAndClickingItReturnsToTheEnd()
    {
        var (window, vm, room, scrollers) = Shown();
        var button = Jump(room);
        Assert.False(button.IsVisible);

        scrollers[0].Offset = new Vector(0, 100);
        Settle(window);
        Say(vm, 100);
        Settle(window);

        Assert.True(button.IsVisible);
        Assert.Equal(button.Width, button.Height);
        Assert.Equal(button.Width / 2, button.CornerRadius.TopLeft);

        button.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Settle(window);

        Assert.False(button.IsVisible);
        Assert.All(scrollers, s => Assert.Equal(s.Extent.Height - s.Viewport.Height, s.Offset.Y, 1));
        window.Close();
    }

    [AvaloniaFact]
    public void FollowingTheLiveEdgeShowsNoButton()
    {
        var (window, vm, room, scrollers) = Shown();

        Say(vm, 100);
        Settle(window);

        Assert.False(Jump(room).IsVisible);
        Assert.All(scrollers, s => Assert.Equal(s.Extent.Height - s.Viewport.Height, s.Offset.Y, 1));
        window.Close();
    }

    private static (Window Window, MainViewModel Vm, ScrollViewer[] Scrollers) ShownWithRuler()
    {
        var (window, vm, _, scrollers) = Shown();
        for (var n = 40; n < 120; n++)
            Say(vm, n);
        for (var n = 0; n < 120; n++)
            vm.Ruler.Observe(new Utterance($"u{n}", "s1", n * 1000, n * 1000 + 900, "zh", $"Zeile {n}", 1,
                UtteranceState.Final, false, 1.0, new Dictionary<string, string>()));
        Settle(window);
        return (window, vm, scrollers);
    }

    private static List<string> InView(MainViewModel vm) =>
        vm.Ruler.Ticks.Where(t => t.IsInView).Select(t => t.AnchorUtteranceId).ToList();

    private static Button RulerArrow(Window window, string name) =>
        window.GetVisualDescendants().OfType<Button>().Single(b => b.Name == name);

    [AvaloniaFact]
    public void TheRulerMarksTheSentencesThatAreOnScreen()
    {
        var (window, vm, scrollers) = ShownWithRuler();

        Assert.Contains("u119", InView(vm));
        Assert.DoesNotContain("u0", InView(vm));

        scrollers[0].Offset = new Vector(0, 0);
        Settle(window);

        Assert.Contains("u0", InView(vm));
        Assert.DoesNotContain("u119", InView(vm));
        window.Close();
    }

    [AvaloniaFact]
    public void TheRulerArrowsPageWithoutSkippingASentence()
    {
        var (window, vm, scrollers) = ShownWithRuler();
        scrollers[0].Offset = new Vector(0, 0);
        Settle(window);
        var firstPage = InView(vm);
        var nextUnseen = $"u{firstPage.Count}";
        Assert.DoesNotContain(nextUnseen, firstPage);

        RulerArrow(window, "RulerPageDown").Command!.Execute(null);
        Settle(window);

        var secondPage = InView(vm);
        Assert.Equal(nextUnseen, secondPage[0]);
        Assert.DoesNotContain("u0", secondPage);

        var lastAbove = $"u{int.Parse(secondPage[0][1..]) - 1}";
        RulerArrow(window, "RulerPageUp").Command!.Execute(null);
        Settle(window);

        Assert.Equal(lastAbove, InView(vm)[^1]);
        window.Close();
    }
}
