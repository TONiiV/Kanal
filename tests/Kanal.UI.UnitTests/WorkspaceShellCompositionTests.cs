using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
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

    /// <summary>The floor the window refuses to go below still lays the three regions out.</summary>
    [AvaloniaFact]
    public void TheNarrowestAllowedWindowStillCarriesAllThreeRegions()
    {
        var (window, _) = Shown(WorkspaceShellViewModel.MinShellWidth);

        Assert.True(Region<WorkspaceSidebarView>(window).Bounds.Width >= SidebarViewModel.MinWidth);
        Assert.True(Region<SidePanelView>(window).Bounds.Width >= SidebarViewModel.MinWidth);
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
    /// A collapsed sidebar is only reachable through the toolbar affordance, so that affordance may
    /// not be the part of the toolbar that scrolls out of sight when the window narrows.
    /// </summary>
    [AvaloniaFact]
    public void TheExpandAffordancesAppearOnCollapseAndSitOutsideTheScrollingToolbar()
    {
        var (window, vm) = Shown();
        var iconBar = Region<IconBarView>(window);
        var buttons = iconBar.GetLogicalDescendants().OfType<Button>().ToList();
        var expandLeft = Assert.Single(buttons, button => button.Name == "ExpandWorkspace");
        var expandRight = Assert.Single(buttons, button => button.Name == "ExpandAssistant");
        var scroller = Assert.Single(
            iconBar.GetLogicalDescendants().OfType<ScrollViewer>(),
            view => view.HorizontalScrollBarVisibility == ScrollBarVisibility.Auto);

        Assert.False(expandLeft.IsVisible);
        Assert.False(expandRight.IsVisible);

        vm.Shell.Left.ToggleCommand.Execute(null);
        vm.Shell.Right.ToggleCommand.Execute(null);

        Assert.True(expandLeft.IsVisible);
        Assert.True(expandRight.IsVisible);
        Assert.DoesNotContain(expandLeft, scroller.GetLogicalDescendants());
        Assert.DoesNotContain(expandRight, scroller.GetLogicalDescendants());

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
