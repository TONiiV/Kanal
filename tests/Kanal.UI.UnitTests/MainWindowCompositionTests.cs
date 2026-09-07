using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Shape = Avalonia.Controls.Shapes.Path;
using Avalonia.Threading;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using Avalonia.VisualTree;
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

        Assert.DoesNotContain(
            Bar(window).GetLogicalDescendants().OfType<ScrollViewer>(),
            scroller => scroller.HorizontalScrollBarVisibility != ScrollBarVisibility.Disabled);

        window.Close();
    }

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

        Assert.Single(Bar(window).GetLogicalDescendants().OfType<Button>(), button => button.Name == "JoinQr");
        Assert.Empty(
            window.GetLogicalDescendants().OfType<SidePanelView>().Single()
                .GetLogicalDescendants().OfType<Image>());

        var flags = Assert.Single(
            window.GetLogicalDescendants().OfType<MeetingRoomView>().Single()
                .GetLogicalDescendants().OfType<Button>(),
            button => button.Name == "RoomLanguages");
        // The flags share the title's row now; what has to stay true is that the row is the top
        // of the transcript rather than the toolbar or the side panel.
        Assert.Equal(Dock.Top, DockPanel.GetDock((Control)flags.Parent!));
        Assert.Contains(
            flags.GetLogicalDescendants().OfType<ItemsControl>(),
            items => ReferenceEquals(items.ItemsSource, vm.SelectedLanguages));

        window.Close();
    }

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

    [AvaloniaFact]
    public void TheCapturePickerDrawsItsWholeGlyphInsideItself()
    {
        var vm = TestViewModels.Hermetic();
        vm.SelectedMode = vm.Modes.First(mode => mode.Mode.NeedsMicrophone);
        var window = new MainWindow { DataContext = vm, Width = 1320, Height = 700 };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var capture = Assert.Single(
            Bar(window).GetLogicalDescendants().OfType<ComboBox>(),
            combo => ReferenceEquals(combo.ItemsSource, vm.CaptureProfiles));
        var glyph = Assert.Single(
            capture.GetVisualDescendants().OfType<Shape>(),
            path => path.Classes.Contains("glyph"));

        var slot = glyph.GetVisualAncestors().OfType<Visual>().First(visual => visual.ClipToBounds);
        var corner = glyph.TranslatePoint(new Point(glyph.Bounds.Width, glyph.Bounds.Height), slot);

        Assert.NotNull(corner);
        Assert.True(
            corner.Value.X <= slot.Bounds.Width + 0.5 && corner.Value.Y <= slot.Bounds.Height + 0.5,
            $"the capture glyph runs to {corner.Value} in a {slot.Bounds.Size} slot, so it is cut off.");

        window.Close();
    }

    // Flyout content is outside the logical tree: its bindings stay unevaluated until ShowAt.
    private static T Opened<T>(Button owner) where T : FlyoutBase
    {
        var flyout = Assert.IsType<T>(owner.Flyout);
        flyout.ShowAt(owner);
        Dispatcher.UIThread.RunJobs();
        return flyout;
    }
}
