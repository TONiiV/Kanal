using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Threading;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using Kanal.Host.Views;

namespace Kanal.UI.UnitTests;

public class MainWindowCompositionTests
{
    private static IconBarView Bar(Window window) =>
        window.GetLogicalDescendants().OfType<IconBarView>().Single();

    private static StackPanel Cluster(Window window, string name) =>
        Assert.Single(
            Bar(window).GetLogicalDescendants().OfType<StackPanel>(),
            panel => panel.Name == name);

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

        var iconBar = Bar(window);
        Assert.DoesNotContain(
            iconBar.GetLogicalDescendants().OfType<TextBlock>(),
            text => string.Equals(text.Text, "KANAL", StringComparison.Ordinal));

        var buttons = iconBar.GetLogicalDescendants().OfType<Button>().ToList();
        Assert.Contains(buttons, button => ReferenceEquals(button.Command, vm.StartCommand));
        Assert.Contains(buttons, button => ReferenceEquals(button.Command, vm.PauseCommand));
        Assert.Contains(buttons, button => ReferenceEquals(button.Command, vm.StopCommand));

        window.Close();
    }

    /// <summary>
    /// The bar reads as three regions, and which one a control belongs to is the whole point of the
    /// arrangement: mode and capture on the left, the transport in the middle, the join code on the
    /// right. Tree order is asserted too — it is what Tab and a screen reader follow.
    /// </summary>
    [AvaloniaFact]
    public void TheBarIsThreeClustersWithTheTransportInTheMiddle()
    {
        var vm = TestViewModels.Hermetic();
        vm.SelectedMode = vm.Modes.First(mode => mode.Mode.NeedsMicrophone);
        var window = new MainWindow { DataContext = vm };
        window.Show();

        var left = Cluster(window, "LeftCluster");
        var transport = Cluster(window, "Transport");
        var right = Cluster(window, "RightCluster");

        Assert.Equal(0, Grid.GetColumn(left));
        Assert.Equal(1, Grid.GetColumn(transport));
        Assert.Equal(2, Grid.GetColumn(right));

        Assert.Contains(
            left.GetLogicalDescendants().OfType<ComboBox>(),
            combo => ReferenceEquals(combo.ItemsSource, vm.Modes));
        Assert.Contains(
            left.GetLogicalDescendants().OfType<ComboBox>(),
            combo => ReferenceEquals(combo.ItemsSource, vm.CaptureProfiles));

        var marks = transport.GetLogicalDescendants().OfType<Button>().ToList();
        Assert.Contains(marks, button => ReferenceEquals(button.Command, vm.StartCommand));
        Assert.Contains(marks, button => ReferenceEquals(button.Command, vm.PauseCommand));
        Assert.Contains(marks, button => ReferenceEquals(button.Command, vm.StopCommand));
        Assert.Single(transport.GetLogicalDescendants().OfType<Button>(), b => b.Name == "AudioDevices");

        Assert.Single(right.GetLogicalDescendants().OfType<Button>(), button => button.Name == "JoinQr");

        var grid = Assert.IsType<Grid>(left.Parent);
        Assert.Equal([left, transport, right], grid.Children);

        window.Close();
    }

    /// <summary>
    /// The narrowing rule, which is the one claim the column widths exist to make: the transport
    /// keeps every pixel it asked for while the bar as a whole stays inside the window. The side
    /// clusters give the space up instead — measured, because nothing else would catch a starred
    /// column quietly turning into an Auto one.
    /// </summary>
    [AvaloniaFact]
    public void TheTransportKeepsItsWidthAsTheWindowNarrowsAndTheBarNeverScrolls()
    {
        var vm = TestViewModels.Hermetic();
        vm.SelectedMode = vm.Modes.First(mode => mode.Mode.NeedsMicrophone);
        var window = new MainWindow { DataContext = vm, Width = 2200, Height = 700 };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var wide = Cluster(window, "Transport").Bounds.Width;
        Assert.True(wide > 0, "the transport measured nothing at 2200 px.");

        foreach (var width in new[] { 1320.0, 900.0 })
        {
            window.Width = width;
            Dispatcher.UIThread.RunJobs();

            var transport = Cluster(window, "Transport");
            Assert.Equal(wide, transport.Bounds.Width, precision: 1);

            var bar = Assert.IsType<Grid>(transport.Parent);
            Assert.True(
                transport.Bounds.X >= 0 && transport.Bounds.Right <= bar.Bounds.Width + 1,
                $"the transport left the bar at {width} px: {transport.Bounds} in {bar.Bounds}");
        }

        // Scrolling the bar sideways was the previous answer to a narrow window, and it hid the
        // controls it was meant to preserve.
        Assert.DoesNotContain(
            Bar(window).GetLogicalDescendants().OfType<ScrollViewer>(),
            scroller => scroller.HorizontalScrollBarVisibility != ScrollBarVisibility.Disabled);

        window.Close();
    }

    /// <summary>
    /// Both marks are one button each, shown and hidden rather than enabled and disabled, so the
    /// bindings that decide which is on screen are what the operator actually reads.
    /// </summary>
    [AvaloniaFact]
    public void TheMarksOnScreenFollowTheMeetingState()
    {
        var vm = TestViewModels.Hermetic();
        var window = new MainWindow { DataContext = vm };
        window.Show();

        var transport = Cluster(window, "Transport");
        var record = Assert.Single(transport.GetLogicalDescendants().OfType<Button>(), b => b.Name == "RecordMark");
        var pause = Assert.Single(transport.GetLogicalDescendants().OfType<Button>(), b => b.Name == "PauseMark");
        var stop = Assert.Single(transport.GetLogicalDescendants().OfType<Button>(), b => b.Name == "StopMark");

        Assert.True(record.IsVisible);
        Assert.False(pause.IsVisible);
        Assert.False(stop.IsVisible);

        vm.IsRunning = true;
        Dispatcher.UIThread.RunJobs();

        Assert.False(record.IsVisible);
        Assert.True(pause.IsVisible);
        Assert.True(stop.IsVisible);

        window.Close();
    }

    /// <summary>
    /// Three controls left the bar in this rearrangement, and each would be unreachable rather than
    /// merely moved if its new home were forgotten.
    /// </summary>
    [AvaloniaFact]
    public void SettingsTheJoinCodeAndTheFlagsMovedOutOfTheirOldHomes()
    {
        var vm = TestViewModels.Hermetic();
        var window = new MainWindow { DataContext = vm };
        window.Show();

        var workspace = window.GetLogicalDescendants().OfType<WorkspaceSidebarView>().Single();
        var settings = Assert.Single(
            workspace.GetLogicalDescendants().OfType<Button>(),
            button => button.Name == "Settings");
        Assert.Equal(Dock.Bottom, DockPanel.GetDock(settings));
        Assert.DoesNotContain(
            Bar(window).GetLogicalDescendants().OfType<Button>(),
            button => button.Name == "Settings");

        // The join code was in the assistant sidebar, which #71 rebuilds around something else.
        Assert.Single(Bar(window).GetLogicalDescendants().OfType<Button>(), button => button.Name == "JoinQr");
        Assert.Empty(
            window.GetLogicalDescendants().OfType<SidePanelView>().Single()
                .GetLogicalDescendants().OfType<Image>());

        // The flags belong to the meeting, so they sit at the head of the transcript.
        var flags = Assert.Single(
            window.GetLogicalDescendants().OfType<MeetingRoomView>().Single()
                .GetLogicalDescendants().OfType<Button>(),
            button => button.Name == "RoomLanguages");
        Assert.Equal(Dock.Top, DockPanel.GetDock(flags));
        Assert.Contains(
            flags.GetLogicalDescendants().OfType<ItemsControl>(),
            items => ReferenceEquals(items.ItemsSource, vm.SelectedLanguages));

        window.Close();
    }

    /// <summary>
    /// The device pickers and the export commands both moved behind a mark, so what they are worth
    /// depends entirely on the flyout opening onto them. Nothing in the bar's own tree would show
    /// that, so the flyouts are opened.
    /// </summary>
    [AvaloniaFact]
    public void TheFlyoutsBehindTheMarksCarryTheDevicesAndTheExports()
    {
        var vm = TestViewModels.Hermetic();
        vm.SelectedMode = vm.Modes.First(mode => mode.Mode.NeedsMicrophone);
        var window = new MainWindow { DataContext = vm };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var audio = Assert.Single(
            Bar(window).GetLogicalDescendants().OfType<Button>(),
            button => button.Name == "AudioDevices");
        var pickers = Opened<Flyout>(audio).Content as Control;
        Assert.NotNull(pickers);
        Assert.Contains(
            pickers.GetLogicalDescendants().OfType<ComboBox>(),
            combo => ReferenceEquals(combo.ItemsSource, vm.Devices));

        var more = Assert.Single(
            Bar(window).GetLogicalDescendants().OfType<Button>(),
            button => button.Name == "MeetingMore");
        var commands = Opened<MenuFlyout>(more).Items.OfType<MenuItem>()
            .Select(item => item.Command).ToList();
        Assert.Contains(commands, command => ReferenceEquals(command, vm.ExportMarkdownCommand));
        Assert.Contains(commands, command => ReferenceEquals(command, vm.ExportJsonCommand));

        window.Close();
    }

    private static T Opened<T>(Button owner) where T : FlyoutBase
    {
        var flyout = Assert.IsType<T>(owner.Flyout);
        flyout.ShowAt(owner);
        Dispatcher.UIThread.RunJobs();
        return flyout;
    }
}
