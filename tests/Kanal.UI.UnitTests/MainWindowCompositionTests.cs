using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Threading;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using Kanal.Host.Views;

namespace Kanal.UI.UnitTests;

public class MainWindowCompositionTests
{
    [AvaloniaFact]
    public void MainWindowComposesTheFourNamedRegionsWithoutAWordmark()
    {
        var vm = TestViewModels.Hermetic();
        var window = new MainWindow { DataContext = vm };
        window.Show();

        Assert.Single(window.GetLogicalDescendants().OfType<IconBarView>());
        Assert.Single(window.GetLogicalDescendants().OfType<MeetingRoomView>());
        Assert.Single(window.GetLogicalDescendants().OfType<SidePanelView>());
        Assert.Single(window.GetLogicalDescendants().OfType<StatusBarView>());

        var iconBar = window.GetLogicalDescendants().OfType<IconBarView>().Single();
        Assert.DoesNotContain(
            iconBar.GetLogicalDescendants().OfType<TextBlock>(),
            text => string.Equals(text.Text, "KANAL", StringComparison.Ordinal));

        var toolbarScroller = Assert.Single(
            iconBar.GetLogicalDescendants().OfType<ScrollViewer>(),
            scroller => scroller.HorizontalScrollBarVisibility == ScrollBarVisibility.Auto);
        Assert.Equal(ScrollBarVisibility.Disabled, toolbarScroller.VerticalScrollBarVisibility);

        var buttons = iconBar.GetLogicalDescendants().OfType<Button>().ToList();
        Assert.Contains(buttons, button => ReferenceEquals(button.Command, vm.StartCommand));
        Assert.Contains(buttons, button => ReferenceEquals(button.Command, vm.PauseCommand));
        Assert.Contains(buttons, button => ReferenceEquals(button.Command, vm.StopCommand));
        Assert.Contains(buttons, button => ReferenceEquals(button.Command, vm.ExportMarkdownCommand));

        window.Close();
    }

    /// <summary>
    /// The bar reads as two clusters: what drives the meeting on the left, what is set up once on
    /// the right. Which panel a control belongs to is the whole point of the arrangement, so it is
    /// asserted here rather than left to a pixel comparison.
    /// </summary>
    [AvaloniaFact]
    public void TheToolbarSplitsIntoALeftMeetingClusterAndARightSetupCluster()
    {
        var vm = TestViewModels.Hermetic();
        var window = new MainWindow { DataContext = vm };
        window.Show();

        var iconBar = window.GetLogicalDescendants().OfType<IconBarView>().Single();
        var panels = iconBar.GetLogicalDescendants().OfType<StackPanel>().ToList();
        var left = Assert.Single(panels, panel => panel.Name == "LeftCluster");
        var right = Assert.Single(panels, panel => panel.Name == "RightCluster");

        Assert.Equal(Dock.Left, DockPanel.GetDock(left));
        Assert.Equal(Dock.Right, DockPanel.GetDock(right));

        var leftButtons = left.GetLogicalDescendants().OfType<Button>().ToList();
        Assert.Contains(leftButtons, button => ReferenceEquals(button.Command, vm.StartCommand));
        Assert.Contains(leftButtons, button => ReferenceEquals(button.Command, vm.PauseCommand));
        Assert.Contains(leftButtons, button => ReferenceEquals(button.Command, vm.StopCommand));
        Assert.Contains(
            left.GetLogicalDescendants().OfType<ComboBox>(),
            combo => ReferenceEquals(combo.ItemsSource, vm.Modes));

        Assert.Contains(
            right.GetLogicalDescendants().OfType<Button>(),
            button => ReferenceEquals(button.Command, vm.ExportMarkdownCommand));
        Assert.Contains(
            right.GetLogicalDescendants().OfType<ComboBox>(),
            combo => ReferenceEquals(combo.ItemsSource, vm.Devices));

        // Tree order, not laid-out position, is what Tab and a screen reader follow: the operator
        // must meet the transport before Export and Settings.
        var dock = Assert.IsType<DockPanel>(left.Parent);
        Assert.Equal([left, right], dock.Children);

        window.Close();
    }

    /// <summary>
    /// The two claims the arrangement rests on, and the only two that can fail silently: the right
    /// cluster holds the viewport edge while the bar fits, and the clusters keep a gap once it does
    /// not. Measured rather than compared to a picture - the numbers are the behaviour.
    /// </summary>
    [AvaloniaTheory]
    [InlineData(1800.0)]
    [InlineData(1280.0)]
    [InlineData(900.0)]
    public void TheRightClusterHoldsTheEdgeWhileTheBarFitsAndTheClustersNeverMeet(double width)
    {
        var vm = TestViewModels.Hermetic();
        vm.SelectedMode = vm.Modes.First(mode => mode.Mode.NeedsMicrophone);
        var window = new MainWindow { DataContext = vm, Width = width, Height = 700 };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var iconBar = window.GetLogicalDescendants().OfType<IconBarView>().Single();
        var panels = iconBar.GetLogicalDescendants().OfType<StackPanel>().ToList();
        var left = Assert.Single(panels, panel => panel.Name == "LeftCluster");
        var right = Assert.Single(panels, panel => panel.Name == "RightCluster");
        var dock = Assert.IsType<DockPanel>(left.Parent);
        var scroller = Assert.Single(
            iconBar.GetLogicalDescendants().OfType<ScrollViewer>(),
            view => view.HorizontalScrollBarVisibility == ScrollBarVisibility.Auto);

        var gap = right.Bounds.X - left.Bounds.Right;
        Assert.True(gap >= 8, $"clusters are {gap} apart at {width} px");

        // While it fits, the right cluster ends where the viewport does. Once it does not, the bar
        // is wider than the viewport and scrolls - which is the other half of the arrangement.
        if (dock.Bounds.Width <= scroller.Viewport.Width)
            Assert.Equal(scroller.Viewport.Width, right.Bounds.Right, precision: 1);
        else
            Assert.True(scroller.Extent.Width > scroller.Viewport.Width);

        window.Close();
    }
}
