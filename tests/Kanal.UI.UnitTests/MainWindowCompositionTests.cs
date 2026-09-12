using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Shape = Avalonia.Controls.Shapes.Path;
using Avalonia.Threading;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.VisualTree;
using Kanal.Host.Controls;
using Kanal.Host.Localization;
using Kanal.Host.Views;

namespace Kanal.UI.UnitTests;

public class MainWindowCompositionTests
{
    private static IconBarView Bar(Window window) =>
        window.GetLogicalDescendants().OfType<IconBarView>().Single();

    // Bounds carries the offset inside the parent and TransformToVisual adds it again, so the
    // rectangle handed to the transform has to start at the origin.
    private static Rect Painted(Visual visual, Visual within) =>
        new Rect(visual.Bounds.Size).TransformToAABB(visual.TransformToVisual(within)!.Value);

    private static Panel Cluster(Window window, string name) =>
        Assert.Single(
            Bar(window).GetLogicalDescendants().OfType<Panel>(),
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

        window.Close();
    }

    /// <summary>
    /// Hover grows the mark, so it has to grow about the mark's own centre: an origin anywhere
    /// else moves the disc across the bar on the way up.
    /// </summary>
    [AvaloniaFact]
    public void HoveringATransportMarkGrowsItAboutItsOwnCentre()
    {
        var vm = TestViewModels.Hermetic();
        vm.SelectedMode = vm.Modes.First(mode => mode.Mode.NeedsMicrophone);
        var window = new MainWindow { DataContext = vm, Width = 1320, Height = 700 };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var mark = Assert.Single(Bar(window).GetLogicalDescendants().OfType<Button>(),
            button => button.Name == "RecordMark");
        var bar = Bar(window);
        var middle = new Point(mark.Bounds.Width / 2, mark.Bounds.Height / 2);
        var atRest = mark.TranslatePoint(middle, bar)!.Value;

        ((IPseudoClasses)mark.Classes).Set(":pointerover", true);
        Dispatcher.UIThread.RunJobs();

        var hovered = mark.TranslatePoint(middle, bar)!.Value;
        Assert.Equal(atRest.X, hovered.X, precision: 2);
        Assert.Equal(atRest.Y, hovered.Y, precision: 2);

        var glyph = Assert.Single(mark.GetVisualDescendants().OfType<Shape>(), path => path.IsVisible);
        var offset = glyph.TranslatePoint(new Point(glyph.Bounds.Width / 2, glyph.Bounds.Height / 2), mark)!.Value
            - middle;
        Assert.True(
            Math.Abs(offset.X) < 0.01 && Math.Abs(offset.Y) < 0.01,
            $"hovered, the glyph sits {offset} off the centre of its {mark.Bounds.Size} disc.");

        window.Close();
    }

    /// <summary>
    /// A glyph box an odd number of pixels wide leaves half a pixel of slack on one side of an
    /// even disc, which is how the transport ended up with the pause mark a hair right of centre
    /// and the stop mark a hair left.
    /// </summary>
    [AvaloniaFact]
    public void EveryTransportMarkSitsInTheCentreOfItsDisc()
    {
        var vm = TestViewModels.Hermetic();
        vm.SelectedMode = vm.Modes.First(mode => mode.Mode.NeedsMicrophone);
        vm.IsRunning = true;
        vm.IsTranscribing = true;
        var window = new MainWindow { DataContext = vm, Width = 1320, Height = 700 };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var marks = Bar(window).GetLogicalDescendants().OfType<Button>()
            .Where(button => button.Classes.Contains("mark") && button.IsVisible)
            .ToList();

        Assert.NotEmpty(marks);
        Assert.All(marks, mark =>
        {
            var glyph = Assert.Single(
                mark.GetVisualDescendants().OfType<Shape>(),
                path => path.IsVisible);
            var centre = new Point(glyph.Bounds.Width / 2, glyph.Bounds.Height / 2);
            var offset = glyph.TranslatePoint(centre, mark)!.Value
                - new Point(mark.Bounds.Width / 2, mark.Bounds.Height / 2);

            Assert.True(
                Math.Abs(offset.X) < 0.01 && Math.Abs(offset.Y) < 0.01,
                $"{mark.Name}'s glyph sits {offset} off the centre of its {mark.Bounds.Size} disc.");
        });

        window.Close();
    }

    /// <summary>
    /// The chevron is drawn by the ComboBox template against the picker's own right edge, so a
    /// picker sized to its glyph alone loses it: the width has to carry glyph, gap and chevron.
    /// </summary>
    [AvaloniaFact]
    public void TheCapturePickerDrawsItsChevronInsideItselfWithoutCrowdingTheGlyph()
    {
        var vm = TestViewModels.Hermetic();
        vm.SelectedMode = vm.Modes.First(mode => mode.Mode.NeedsMicrophone);
        var window = new MainWindow { DataContext = vm, Width = 1320, Height = 700 };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var capture = Assert.Single(
            Bar(window).GetLogicalDescendants().OfType<ComboBox>(),
            combo => ReferenceEquals(combo.ItemsSource, vm.CaptureProfiles));
        var chevron = Assert.Single(capture.GetVisualDescendants().OfType<PathIcon>());
        var glyph = Assert.Single(
            capture.GetVisualDescendants().OfType<Shape>(),
            path => path.Classes.Contains("glyph") && path.IsVisible);

        var left = chevron.TranslatePoint(new Point(0, 0), capture)!.Value.X;
        var right = chevron.TranslatePoint(new Point(chevron.Bounds.Width, 0), capture)!.Value.X;
        var glyphRight = glyph.TranslatePoint(new Point(glyph.Bounds.Width, 0), capture)!.Value.X;

        Assert.True(right <= capture.Bounds.Width,
            $"the chevron runs to {right} in a {capture.Bounds.Width} picker, so it is cut off.");
        Assert.True(left - glyphRight >= 4,
            $"only {left - glyphRight} between the capture glyph and its chevron.");

        window.Close();
    }

    /// <summary>
    /// The left cluster is narrower than what it holds as soon as the window is, and it used to
    /// give up the width at its right-hand end — where the capture picker is. The mode label is
    /// the only thing here that can be shortened, so it is the only thing that yields.
    /// </summary>
    [AvaloniaTheory]
    [InlineData("en")]
    [InlineData("pl")]
    public void TheModeLabelGivesUpWidthBeforeTheCapturePickerDoes(string code)
    {
        var previous = Localizer.Instance.Current;
        try
        {
            Localizer.Instance.Current = code;
            var vm = TestViewModels.Hermetic();
            vm.SelectedMode = vm.Modes.Last(mode => mode.Mode.NeedsMicrophone);
            vm.IsRunning = true;
            vm.IsTranscribing = true;
            var window = new MainWindow { DataContext = vm, Width = 1320, Height = 700 };
            window.Show();
            Dispatcher.UIThread.RunJobs();

            var cluster = Cluster(window, "LeftCluster");
            var capture = Assert.Single(
                cluster.GetLogicalDescendants().OfType<ComboBox>(),
                combo => ReferenceEquals(combo.ItemsSource, vm.CaptureProfiles));
            var modes = Assert.Single(
                cluster.GetLogicalDescendants().OfType<ComboBox>(),
                combo => ReferenceEquals(combo.ItemsSource, vm.Modes));
            var left = capture.TranslatePoint(new Point(0, 0), cluster)!.Value.X;
            var right = capture.TranslatePoint(new Point(capture.Bounds.Width, 0), cluster)!.Value.X;
            var modesRight = modes.TranslatePoint(new Point(modes.Bounds.Width, 0), cluster)!.Value.X;

            Assert.True(
                right <= cluster.Bounds.Width + 0.5,
                $"{code}: the capture picker ends at {right} in a {cluster.Bounds.Width} cluster.");
            Assert.True(
                modesRight <= left + 0.5,
                $"{code}: the mode box runs to {modesRight}, over a capture picker that starts at {left}.");

            window.Close();
        }
        finally
        {
            Localizer.Instance.Current = previous;
        }
    }

    /// <summary>
    /// The collapsed mode box used to draw one chip whatever the mode was, which said nothing
    /// about where either half of the pipeline runs — the one thing the mode names.
    /// </summary>
    [AvaloniaFact]
    public void TheModeBoxMarksTheModeItIsOn()
    {
        var vm = TestViewModels.Hermetic();
        var window = new MainWindow { DataContext = vm, Width = 1320, Height = 700 };
        window.Show();

        var modes = Assert.Single(
            Bar(window).GetLogicalDescendants().OfType<ComboBox>(),
            combo => ReferenceEquals(combo.ItemsSource, vm.Modes));

        foreach (var option in vm.Modes)
        {
            vm.SelectedMode = option;
            Dispatcher.UIThread.RunJobs();

            var mark = Assert.Single(modes.GetVisualDescendants().OfType<Shape>()
                .Where(path => path.Classes.Contains("mode") && path.IsVisible));

            Assert.Equal(Icons.Mode(option.Mode.Id), mark.Data);
        }

        window.Close();
    }

    /// <summary>
    /// The open list and the collapsed box have to agree, or the mark the operator learns in the
    /// list means nothing back in the bar.
    /// </summary>
    [AvaloniaFact]
    public void TheModeListLeadsEveryRowWithThatModesOwnMark()
    {
        var vm = TestViewModels.Hermetic();
        var window = new MainWindow { DataContext = vm, Width = 1320, Height = 700 };
        window.Show();

        var modes = Assert.Single(
            Bar(window).GetLogicalDescendants().OfType<ComboBox>(),
            combo => ReferenceEquals(combo.ItemsSource, vm.Modes));
        modes.IsDropDownOpen = true;
        Dispatcher.UIThread.RunJobs();

        foreach (var (option, index) in vm.Modes.Select((option, index) => (option, index)))
        {
            var row = Assert.IsType<ComboBoxItem>(modes.ContainerFromIndex(index));
            var mark = Assert.Single(row.GetVisualDescendants().OfType<Shape>()
                .Where(path => path.Classes.Contains("mode")));

            Assert.Equal(Icons.Mode(option.Mode.Id), mark.Data);
        }

        window.Close();
    }

    /// <summary>
    /// The help button explains the mode, so it has to belong to the mode box. In an uncapped
    /// starred column the box sits at the left of whatever width the window has spare and the
    /// button lands at the far right of it, next to the transport it says nothing about.
    /// </summary>
    [AvaloniaTheory]
    [InlineData(1320.0)]
    [InlineData(1600.0)]
    public void TheHelpButtonStaysAgainstTheModeBox(double width)
    {
        var vm = TestViewModels.Hermetic();
        var window = new MainWindow { DataContext = vm, Width = width, Height = 700 };
        window.Show();

        var cluster = Cluster(window, "LeftCluster");
        var modes = Assert.Single(
            cluster.GetLogicalDescendants().OfType<ComboBox>(),
            combo => ReferenceEquals(combo.ItemsSource, vm.Modes));
        var help = Assert.Single(
            cluster.GetLogicalDescendants().OfType<Button>(),
            button => (button.Content as string) == "?");

        var gap = help.TranslatePoint(new Point(0, 0), cluster)!.Value.X
            - modes.TranslatePoint(new Point(modes.Bounds.Width, 0), cluster)!.Value.X;

        Assert.InRange(gap, 0, 9);

        window.Close();
    }

    /// <summary>
    /// A record button reads as a record button when the disc nearly fills it and the wash is
    /// left as a ring, not as a field with a dot in the middle of it.
    /// </summary>
    [AvaloniaFact]
    public void TheRecordDiscNearlyFillsTheButtonItSitsIn()
    {
        var vm = TestViewModels.Hermetic();
        var window = new MainWindow { DataContext = vm, Width = 1320, Height = 700 };
        window.Show();

        var button = Assert.Single(Bar(window).GetLogicalDescendants().OfType<Button>(),
            candidate => candidate.Name == "RecordMark");
        var disc = Assert.Single(button.GetVisualDescendants().OfType<Shape>());
        var ratio = Painted(disc, button).Width / button.Bounds.Width;

        Assert.InRange(ratio, 0.80, 0.88);

        window.Close();
    }

    /// <summary>
    /// Only a fractional render scaling exposes this: an inset of 3 px is 4.5 device pixels at
    /// 1.5x, layout rounding snaps it to 4, and the red disc slides half a device pixel off the
    /// centre of the wash it sits in. Against a 3 px ring that reads as a crooked hole.
    /// </summary>
    [AvaloniaFact]
    public void TheRecordDiscStaysCentredInItsWashAtFractionalScaling()
    {
        var vm = TestViewModels.Hermetic();
        var window = new MainWindow { DataContext = vm, Width = 1320, Height = 700 };
        window.Show();
        window.SetRenderScaling(1.5);
        Dispatcher.UIThread.RunJobs();

        var button = Assert.Single(Bar(window).GetLogicalDescendants().OfType<Button>(),
            candidate => candidate.Name == "RecordMark");
        var disc = Assert.Single(button.GetVisualDescendants().OfType<Shape>());
        var painted = Painted(disc, button);

        Assert.Equal(button.Bounds.Width / 2, painted.Center.X, precision: 2);
        Assert.Equal(button.Bounds.Height / 2, painted.Center.Y, precision: 2);

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
            path => path.Classes.Contains("glyph") && path.IsVisible);

        var slot = glyph.GetVisualAncestors().OfType<Visual>().First(visual => visual.ClipToBounds);
        var corner = glyph.TranslatePoint(new Point(glyph.Bounds.Width, glyph.Bounds.Height), slot);

        Assert.NotNull(corner);
        Assert.True(
            corner.Value.X <= slot.Bounds.Width + 0.5 && corner.Value.Y <= slot.Bounds.Height + 0.5,
            $"the capture glyph runs to {corner.Value} in a {slot.Bounds.Size} slot, so it is cut off.");

        window.Close();
    }

    [AvaloniaFact]
    public void TheCapturePickerShowsAMarkPerProfileWithoutBeingOpened()
    {
        var vm = TestViewModels.Hermetic();
        vm.SelectedMode = vm.Modes.First(mode => mode.Mode.NeedsMicrophone);
        var window = new MainWindow { DataContext = vm, Width = 1320, Height = 700 };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var capture = Assert.Single(
            Bar(window).GetLogicalDescendants().OfType<ComboBox>(),
            combo => ReferenceEquals(combo.ItemsSource, vm.CaptureProfiles));

        Geometry? Shown()
        {
            Dispatcher.UIThread.RunJobs();
            return capture.GetVisualDescendants().OfType<Shape>()
                .Where(path => path.Classes.Contains("glyph") && path.IsVisible)
                .Select(path => path.Data)
                .SingleOrDefault();
        }

        vm.SelectedCaptureProfile = vm.CaptureProfiles.Single(profile => profile.IsInRoom);
        var inRoom = Shown();
        vm.SelectedCaptureProfile = vm.CaptureProfiles.Single(profile => !profile.IsInRoom);
        var online = Shown();

        Assert.NotNull(inRoom);
        Assert.NotNull(online);
        Assert.NotSame(inRoom, online);

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
