using Avalonia.Controls;
using Avalonia.Controls.Primitives;
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
            right.GetLogicalDescendants().OfType<Button>(),
            button => ReferenceEquals(button.Command, vm.ToggleMuteCommand));
        Assert.Contains(
            right.GetLogicalDescendants().OfType<Button>(),
            button => button.Name == "DevicePicker");

        window.Close();
    }
}
