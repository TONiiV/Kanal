using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using Avalonia.VisualTree;
using Avalonia.Threading;
using Kanal.Host.ViewModels;
using Kanal.Host.Views;

namespace Kanal.UI.UnitTests;

public class WorkspaceShellCompositionTests
{
    private static (Window Window, MainViewModel Vm) Shown(double width = 1320)
    {
        var vm = TestViewModels.Hermetic();
        var window = new MainWindow { DataContext = vm, Width = width, Height = 820 };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return (window, vm);
    }

    private static T Region<T>(Window window) where T : Control =>
        window.GetLogicalDescendants().OfType<T>().Single();

    private static Control Named(Control root, string name) =>
        root.GetLogicalDescendants().OfType<Control>().Single(control => control.Name == name);

    [AvaloniaFact]
    public void TheWindowIsThreeDeclaredRegions()
    {
        var (window, _) = Shown();

        Assert.Single(window.GetLogicalDescendants().OfType<WorkspaceSidebarView>());
        Assert.Single(window.GetLogicalDescendants().OfType<MeetingRoomView>());
        Assert.Single(window.GetLogicalDescendants().OfType<SidePanelView>());

        window.Close();
    }

    /// <summary>
    /// The prototype's failure: a sidebar collapsing by hiding its children, which left the
    /// transcript in whatever column was left over. Measured, because the claim is that the
    /// transcript is still usable - not that the XAML says what it says.
    /// </summary>
    [AvaloniaTheory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void TheTranscriptKeepsAUsableWidthInEveryCombination(bool left, bool right)
    {
        var (window, vm) = Shown();

        if (left) vm.Shell.Left.ToggleCommand.Execute(null);
        if (right) vm.Shell.Right.ToggleCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();

        var transcript = Region<MeetingRoomView>(window);
        Assert.True(transcript.IsVisible);
        Assert.True(
            transcript.Bounds.Width >= WorkspaceShellViewModel.TranscriptReserve,
            $"transcript is {transcript.Bounds.Width} px wide with left={left}, right={right}");

        Assert.Equal(!left, Region<WorkspaceSidebarView>(window).IsVisible);
        Assert.Equal(!right, Region<SidePanelView>(window).IsVisible);

        window.Close();
    }

    /// <summary>
    /// The narrowest window each pair of widths allows still lays all three regions out inside it.
    /// Widened sidebars are the case that mattered: against a fixed floor they ran off the right of
    /// the window, taking the only control that reopens the assistant with them.
    /// </summary>
    [AvaloniaTheory]
    [InlineData(SidebarViewModel.MinWidth)]
    [InlineData(SidebarViewModel.DefaultWidth)]
    [InlineData(SidebarViewModel.MaxWidth)]
    public void TheNarrowestAllowedWindowStillCarriesAllThreeRegions(double sidebars)
    {
        var (window, vm) = Shown();
        vm.Shell.Left.Width = sidebars;
        vm.Shell.Right.Width = sidebars;

        window.Width = vm.Shell.MinShellWidth;
        Dispatcher.UIThread.RunJobs();

        var assistant = Region<SidePanelView>(window);
        Assert.True(
            assistant.Bounds.Right <= window.Width + 1,
            $"assistant ends at {assistant.Bounds.Right} in a {window.Width} px window");
        Assert.True(Region<WorkspaceSidebarView>(window).Bounds.Width >= SidebarViewModel.MinWidth);
        Assert.True(assistant.Bounds.Width >= SidebarViewModel.MinWidth);
        Assert.True(
            Region<MeetingRoomView>(window).Bounds.Width >= WorkspaceShellViewModel.TranscriptReserve,
            $"transcript is {Region<MeetingRoomView>(window).Bounds.Width} px at the window floor");

        window.Close();
    }

    /// <summary>
    /// Collapsing gives the released space to the transcript rather than to padding, and expanding
    /// gives it back. Both directions, because only one of them failed in the prototype.
    /// </summary>
    [AvaloniaFact]
    public void CollapsingASidebarHandsItsWidthToTheTranscriptAndExpandingReturnsIt()
    {
        var (window, vm) = Shown();
        var transcript = Region<MeetingRoomView>(window);
        var open = transcript.Bounds.Width;

        vm.Shell.Left.ToggleCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        var collapsed = transcript.Bounds.Width;

        vm.Shell.Left.ToggleCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();

        // The whole sidebar, not merely "wider": a column that collapses to its own minimum instead
        // of to nothing also gets wider, and that is the failure being excluded.
        Assert.True(
            collapsed - open >= SidebarViewModel.DefaultWidth,
            $"transcript gained {collapsed - open} px, not the sidebar's {SidebarViewModel.DefaultWidth}");
        Assert.Equal(open, transcript.Bounds.Width, precision: 1);

        window.Close();
    }

    /// <summary>
    /// "Left and right header bars share the centre toolbar's height so the separators line up."
    /// A shared constant is only a floor: the toolbar outgrows it whenever the selected mode's
    /// description wraps, so the two sidebars follow the bar's measured height instead.
    /// </summary>
    [AvaloniaTheory]
    [InlineData(1320.0)]
    [InlineData(690.0)]
    public void TheThreeHeaderRulesLandOnOneLine(double width)
    {
        var vm = TestViewModels.Hermetic();
        vm.SelectedMode = vm.Modes.First(mode => mode.Mode.NeedsMicrophone);
        var window = new MainWindow { DataContext = vm, Width = width, Height = 820 };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var workspace = Rule<WorkspaceSidebarView>(window);
        Assert.Equal(workspace, Rule<IconBarView>(window), precision: 1);
        Assert.Equal(workspace, Rule<SidePanelView>(window), precision: 1);

        window.Close();
    }

    // All three regions start at the top of the window, so where a header's rule falls is that
    // header's own height.
    private static double Rule<T>(Window window) where T : Control
    {
        var region = Region<T>(window);
        Assert.Equal(0, region.Bounds.Y);
        var headers = region.GetVisualDescendants().OfType<Border>()
            .Where(border => border.BorderThickness.Bottom >= 2 && border.BorderThickness.Top == 0)
            .ToList();
        Assert.NotEmpty(headers);
        return headers[0].Bounds.Height;
    }

    /// <summary>
    /// A collapsed sidebar is only reachable through the toolbar affordance, so that affordance may
    /// not sit in one of the two columns the bar clips as the window narrows.
    /// </summary>
    [AvaloniaFact]
    public void TheExpandAffordancesAppearOnCollapseAndSitOutsideTheClippedColumns()
    {
        var (window, vm) = Shown();
        var iconBar = Region<IconBarView>(window);
        var buttons = iconBar.GetLogicalDescendants().OfType<Button>().ToList();
        var expandLeft = Assert.Single(buttons, button => button.Name == "ExpandWorkspace");
        var expandRight = Assert.Single(buttons, button => button.Name == "ExpandAssistant");
        var clipped = iconBar.GetLogicalDescendants().OfType<Panel>()
            .Where(panel => panel.Name is "LeftCluster" or "RightCluster")
            .ToList();
        Assert.Equal(2, clipped.Count);

        Assert.False(expandLeft.IsVisible);
        Assert.False(expandRight.IsVisible);

        vm.Shell.Left.ToggleCommand.Execute(null);
        vm.Shell.Right.ToggleCommand.Execute(null);

        Assert.True(expandLeft.IsVisible);
        Assert.True(expandRight.IsVisible);
        foreach (var cluster in clipped)
        {
            Assert.DoesNotContain(expandLeft, cluster.GetLogicalDescendants());
            Assert.DoesNotContain(expandRight, cluster.GetLogicalDescendants());
        }

        window.Close();
    }

    [AvaloniaFact]
    public void EachSidebarPutsItsCollapseControlOnTheEdgeThatFacesTheMeeting()
    {
        var (window, _) = Shown();

        var workspace = Region<WorkspaceSidebarView>(window);
        var assistant = Region<SidePanelView>(window);

        Assert.Equal(Dock.Left, DockPanel.GetDock(Named(workspace, "WorkspaceTitle")));
        Assert.Equal(Dock.Right, DockPanel.GetDock(Named(workspace, "CollapseWorkspace")));

        Assert.Equal(Dock.Left, DockPanel.GetDock(Named(assistant, "CollapseAssistant")));
        Assert.Equal(Dock.Right, DockPanel.GetDock(Named(assistant, "AssistantTitle")));

        window.Close();
    }

    [AvaloniaFact]
    public void EachSidebarCarriesItsOwnCollapseControl()
    {
        var (window, vm) = Shown();

        Assert.Same(
            vm.Shell.Left.ToggleCommand,
            Assert.Single(
                Region<WorkspaceSidebarView>(window).GetLogicalDescendants().OfType<Button>(),
                button => button.Name == "CollapseWorkspace").Command);
        Assert.Same(
            vm.Shell.Right.ToggleCommand,
            Assert.Single(
                Region<SidePanelView>(window).GetLogicalDescendants().OfType<Button>(),
                button => button.Name == "CollapseAssistant").Command);

        window.Close();
    }

    [AvaloniaFact]
    public void ASidebarOffersItsResizeHandleOnlyWhileItIsOpen()
    {
        var (window, vm) = Shown();
        var splitters = window.GetLogicalDescendants().OfType<GridSplitter>().ToList();
        var workspace = Assert.Single(splitters, splitter => splitter.Name == "WorkspaceSplitter");
        var assistant = Assert.Single(splitters, splitter => splitter.Name == "AssistantSplitter");

        Assert.True(workspace.IsVisible);
        Assert.True(assistant.IsVisible);

        vm.Shell.Left.ToggleCommand.Execute(null);

        Assert.False(workspace.IsVisible);
        Assert.True(assistant.IsVisible);

        window.Close();
    }
}
