using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Kanal.Host.Controls;

namespace Kanal.UI.UnitTests;

public class ColumnScrollSyncTests
{
    private sealed class Rig
    {
        public required Window Window { get; init; }
        public required ColumnScrollSync Sync { get; init; }
        public required ScrollViewer[] Scrollers { get; init; }
        public required ObservableCollection<double>[] Rows { get; init; }

        public void Settle()
        {
            for (var i = 0; i < 4; i++)
            {
                Dispatcher.UIThread.RunJobs();
                Window.UpdateLayout();
            }
        }

        public void Scroll(int column, double y)
        {
            Scrollers[column].Offset = new Vector(0, y);
            Settle();
        }

        public void Append()
        {
            foreach (var rows in Rows)
                rows.Add(rows[0]);
            Settle();
        }
    }

    // Every column holds the same utterances; a row is 50 DIP in column 0 and 100 DIP in column 1,
    // as a German line is taller than the Chinese one beside it.
    private static Rig Shown(int rowCount = 30, params double[] heights)
    {
        heights = heights.Length == 0 ? [50, 100] : heights;
        var sync = new ColumnScrollSync();
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions(string.Join(",", heights.Select(_ => "*"))) };
        var scrollers = new List<ScrollViewer>();
        var rows = new List<ObservableCollection<double>>();
        for (var column = 0; column < heights.Length; column++)
        {
            var items = new ObservableCollection<double>(Enumerable.Repeat(heights[column], rowCount));
            var list = new ItemsControl
            {
                ItemsSource = items,
                ItemTemplate = new FuncDataTemplate<double>((h, _) => new Border { Height = h }),
            };
            var scroller = new ScrollViewer { Content = list };
            Grid.SetColumn(scroller, column);
            grid.Children.Add(scroller);
            scrollers.Add(scroller);
            rows.Add(items);
        }

        var window = new Window { Width = 600, Height = 400, Content = grid };
        window.Show();
        var rig = new Rig { Window = window, Sync = sync, Scrollers = [.. scrollers], Rows = [.. rows] };
        rig.Settle();
        foreach (var scroller in scrollers)
            sync.Attach(scroller);
        rig.Settle();
        return rig;
    }

    [AvaloniaFact]
    public void ScrollingOneColumnBringsTheSameLineIntoViewInTheOthers()
    {
        var rig = Shown();

        rig.Scroll(0, 500);

        Assert.Equal(500, rig.Scrollers[0].Offset.Y, 1);
        Assert.Equal(1000, rig.Scrollers[1].Offset.Y, 1);

        rig.Scroll(1, 300);

        Assert.Equal(150, rig.Scrollers[0].Offset.Y, 1);
        rig.Window.Close();
    }

    [AvaloniaFact]
    public void ALineHalfwayUpTheTopIsHalfwayUpInTheOthers()
    {
        var rig = Shown();

        rig.Scroll(0, 525);

        Assert.Equal(1050, rig.Scrollers[1].Offset.Y, 1);
        rig.Window.Close();
    }

    [AvaloniaFact]
    public void ReachingTheBottomOfOneColumnReachesTheBottomOfAll()
    {
        var rig = Shown();

        rig.Scrollers[0].ScrollToEnd();
        rig.Settle();

        Assert.True(rig.Sync.IsFollowing);
        Assert.All(rig.Scrollers, s => Assert.Equal(s.Extent.Height - s.Viewport.Height, s.Offset.Y, 1));
        rig.Window.Close();
    }

    [AvaloniaFact]
    public void NewContentKeepsEveryColumnAtTheBottomWhileFollowing()
    {
        var rig = Shown();
        rig.Scrollers[0].ScrollToEnd();
        rig.Settle();

        rig.Append();

        Assert.False(rig.Sync.HasUnseen);
        Assert.All(rig.Scrollers, s => Assert.Equal(s.Extent.Height - s.Viewport.Height, s.Offset.Y, 1));
        rig.Window.Close();
    }

    [AvaloniaFact]
    public void NewContentBelowTheReaderIsSignalledAndLeftAlone()
    {
        var rig = Shown();
        rig.Scroll(0, 300);
        var seen = new List<bool>();
        rig.Sync.Changed += () => seen.Add(rig.Sync.HasUnseen);

        rig.Append();

        Assert.False(rig.Sync.IsFollowing);
        Assert.True(rig.Sync.HasUnseen);
        Assert.Contains(true, seen);
        Assert.Equal(300, rig.Scrollers[0].Offset.Y, 1);
        rig.Window.Close();
    }

    [AvaloniaFact]
    public void JumpingToTheLatestClearsTheSignalAndTakesEveryColumnToTheEnd()
    {
        var rig = Shown();
        rig.Scroll(0, 300);
        rig.Append();

        rig.Sync.ScrollToLatest();
        rig.Settle();

        Assert.False(rig.Sync.HasUnseen);
        Assert.True(rig.Sync.IsFollowing);
        Assert.All(rig.Scrollers, s => Assert.Equal(s.Extent.Height - s.Viewport.Height, s.Offset.Y, 1));
        rig.Window.Close();
    }

    [AvaloniaFact]
    public void ScrollingBackDownByHandToTheBottomClearsTheSignal()
    {
        var rig = Shown();
        rig.Scroll(0, 300);
        rig.Append();

        rig.Scrollers[1].ScrollToEnd();
        rig.Settle();

        Assert.False(rig.Sync.HasUnseen);
        Assert.True(rig.Sync.IsFollowing);
        rig.Window.Close();
    }
}
