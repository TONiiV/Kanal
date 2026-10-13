using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
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
            vm.Ruler.Observe(Final(n));
        Settle(window);
        return (window, vm, scrollers);
    }

    private static Utterance Final(int n) =>
        new($"u{n}", "s1", n * 1000, n * 1000 + 900, "zh", $"Zeile {n}", 1,
            UtteranceState.Final, false, 1.0, new Dictionary<string, string>());

    [AvaloniaFact]
    public void ARoomOpenedWithSentencesInItMarksThemBeforeAnyScroll()
    {
        var vm = TestViewModels.Hermetic();
        foreach (var code in new[] { "zh", "de", "pl" })
            vm.Columns.Add(new ColumnViewModel(code));
        for (var n = 0; n < 40; n++)
        {
            Say(vm, n);
            vm.Ruler.Observe(Final(n));
        }

        var window = new MainWindow { DataContext = vm, Width = 1320, Height = 820 };
        window.Show();
        Settle(window);

        Assert.NotEmpty(InView(vm));
        Assert.True(vm.Ruler.PageDownCommand.CanExecute(null) || vm.Ruler.PageUpCommand.CanExecute(null));
        window.Close();
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

    private static List<int> Whole(ScrollViewer scroller)
    {
        var items = (ItemsControl)scroller.Content!;
        var whole = new List<int>();
        for (var i = 0; i < items.ItemCount; i++)
        {
            if (items.ContainerFromIndex(i) is not Control row || row.TranslatePoint(default, items) is not { } at)
                continue;
            if (at.Y >= scroller.Offset.Y - 0.5 &&
                at.Y + row.Bounds.Height <= scroller.Offset.Y + scroller.Viewport.Height + 0.5)
                whole.Add(i);
        }

        return whole;
    }

    [AvaloniaFact]
    public void TheRulerArrowsPageWithoutSkippingASentence()
    {
        var (window, _, scrollers) = ShownWithRuler();
        scrollers[0].Offset = new Vector(0, 0);
        Settle(window);
        var firstPage = Whole(scrollers[0]);

        RulerArrow(window, "RulerPageDown").Command!.Execute(null);
        Settle(window);

        var secondPage = Whole(scrollers[0]);
        Assert.Equal(firstPage[^1] + 1, secondPage[0]);

        RulerArrow(window, "RulerPageUp").Command!.Execute(null);
        Settle(window);

        Assert.Equal(secondPage[0] - 1, Whole(scrollers[0])[^1]);
        window.Close();
    }

    [AvaloniaFact]
    public void ASentenceCutAtTheBottomIsWhereTheDownArrowLands()
    {
        var (window, _, scrollers) = ShownWithRuler();
        var items = (ItemsControl)scrollers[0].Content!;
        var viewport = scrollers[0].Viewport.Height;
        var cutBottom = Enumerable.Range(0, items.ItemCount).First(i =>
        {
            var row = items.ContainerFromIndex(i)!;
            return row.TranslatePoint(default, items)!.Value.Y + row.Bounds.Height / 2 > viewport;
        });
        var cut = items.ContainerFromIndex(cutBottom)!;
        scrollers[0].Offset = new Vector(
            0, cut.TranslatePoint(default, items)!.Value.Y + cut.Bounds.Height / 2 - viewport);
        Settle(window);
        Assert.DoesNotContain(cutBottom, Whole(scrollers[0]));

        RulerArrow(window, "RulerPageDown").Command!.Execute(null);
        Settle(window);

        Assert.Equal(cutBottom, Whole(scrollers[0])[0]);
        window.Close();
    }

    [AvaloniaFact]
    public void EveryRulerArrowSitsInTheCentreOfItsButton()
    {
        var (window, _, _) = ShownWithRuler();

        foreach (var name in new[] { "RulerPageUp", "RulerPageDown" })
        {
            var button = RulerArrow(window, name);
            Assert.True(button.Bounds.Width > 0, $"{name} has no width.");
            var glyph = Assert.Single(button.GetVisualDescendants().OfType<Shape>());
            var offset = glyph.TranslatePoint(new Point(glyph.Bounds.Width / 2, glyph.Bounds.Height / 2), button)!.Value
                - new Point(button.Bounds.Width / 2, button.Bounds.Height / 2);
            Assert.True(
                Math.Abs(offset.X) < 0.01 && Math.Abs(offset.Y) < 0.01,
                $"the {name} glyph sits {offset} off the centre of its {button.Bounds.Size} button.");
        }

        window.Close();
    }

    private static Point CentreOf(Control control, Visual relativeTo) =>
        control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), relativeTo)!.Value;

    [AvaloniaFact]
    public void TheRulerArrowsAndEveryTickShareOneCentreLine()
    {
        var (window, vm, _) = ShownWithRuler();
        var ticks = window.GetVisualDescendants().OfType<Rectangle>().Where(r => r.Classes.Contains("tick")).ToList();
        vm.Ruler.Hover(vm.Ruler.Ticks[60]);
        Settle(window);

        var axis = CentreOf(RulerArrow(window, "RulerPageUp"), window).X;
        Assert.Equal(axis, CentreOf(RulerArrow(window, "RulerPageDown"), window).X, 2);
        Assert.All(ticks, tick => Assert.Equal(axis, CentreOf(tick, window).X, 2));
        window.Close();
    }

    [AvaloniaFact]
    public void TheDownArrowFollowsTheLastTickAsTheUpArrowLeadsTheFirst()
    {
        var vm = TestViewModels.Hermetic();
        foreach (var code in new[] { "zh", "de", "pl" })
            vm.Columns.Add(new ColumnViewModel(code));
        for (var n = 0; n < 5; n++)
        {
            Say(vm, n);
            vm.Ruler.Observe(Final(n));
        }

        var window = new MainWindow { DataContext = vm, Width = 1320, Height = 820 };
        window.Show();
        Settle(window);

        var ticks = window.GetVisualDescendants().OfType<Rectangle>().Where(r => r.Classes.Contains("tick")).ToList();
        var up = RulerArrow(window, "RulerPageUp");
        var down = RulerArrow(window, "RulerPageDown");
        var above = CentreOf(ticks[0], window).Y - up.TranslatePoint(new Point(0, up.Bounds.Height), window)!.Value.Y;
        var below = down.TranslatePoint(default, window)!.Value.Y - CentreOf(ticks[^1], window).Y;

        Assert.Equal(above, below, 1);
        window.Close();
    }

    [AvaloniaFact]
    public void ThePointerOverTheRulerRaisesTheTicksUnderItAndLeavingReleasesThem()
    {
        var (window, vm, _) = ShownWithRuler();
        var ticks = window.GetVisualDescendants().OfType<Rectangle>().Where(r => r.Classes.Contains("tick")).ToList();
        var target = ticks[60];

        window.MouseMove(CentreOf(target, window));
        Settle(window);

        Assert.Equal(TranscriptRulerViewModel.PeakReach, vm.Ruler.Ticks[60].Reach, 0);
        Assert.True(vm.Ruler.Ticks[60].Reach > vm.Ruler.Ticks[58].Reach);
        Assert.True(vm.Ruler.Ticks[58].Reach > vm.Ruler.Ticks[55].Reach);
        Assert.Equal(TranscriptRulerViewModel.RestReach, vm.Ruler.Ticks[10].Reach);

        window.MouseMove(new Point(20, 400));
        Settle(window);

        Assert.All(vm.Ruler.Ticks.Where(t => !t.IsInView), t =>
            Assert.Equal(TranscriptRulerViewModel.RestReach, t.Reach));
        window.Close();
    }
}
