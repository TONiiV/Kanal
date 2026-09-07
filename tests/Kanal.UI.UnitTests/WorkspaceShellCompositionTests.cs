using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using Kanal.Host.ViewModels;
using Kanal.Host.Views;

namespace Kanal.UI.UnitTests;

public class WorkspaceShellCompositionTests
{
    private static (Window Window, MainViewModel Vm, Grid Shell) Shown()
    {
        var vm = TestViewModels.Hermetic();
        var window = new MainWindow { DataContext = vm };
        window.Show();
        var shell = Assert.Single(
            window.GetLogicalDescendants().OfType<Grid>(), grid => grid.Name == "Shell");
        return (window, vm, shell);
    }

    [AvaloniaFact]
    public void TheWindowIsThreeDeclaredRegions()
    {
        var (window, _, shell) = Shown();

        Assert.Single(window.GetLogicalDescendants().OfType<WorkspaceSidebarView>());
        Assert.Single(window.GetLogicalDescendants().OfType<MeetingRoomView>());
        Assert.Single(window.GetLogicalDescendants().OfType<SidePanelView>());
        Assert.Equal(5, shell.ColumnDefinitions.Count);

        window.Close();
    }

    /// <summary>
    /// The prototype's failure: a sidebar collapsing by hiding its children, which left the
    /// transcript in whatever column was left over. Only the centre is starred, and it has a floor.
    /// </summary>
    [AvaloniaTheory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void TheTranscriptKeepsItsColumnInEveryCombination(bool left, bool right)
    {
        var (window, vm, shell) = Shown();

        if (left) vm.Shell.ToggleLeftCommand.Execute(null);
        if (right) vm.Shell.ToggleRightCommand.Execute(null);

        var centre = shell.ColumnDefinitions[2];
        Assert.True(centre.Width.IsStar);
        Assert.Equal(WorkspaceShellViewModel.TranscriptReserve, centre.MinWidth);

        Assert.Equal(left ? 0 : WorkspaceShellViewModel.DefaultWidth, shell.ColumnDefinitions[0].Width.Value);
        Assert.Equal(right ? 0 : WorkspaceShellViewModel.DefaultWidth, shell.ColumnDefinitions[4].Width.Value);

        Assert.Equal(!left, window.GetLogicalDescendants().OfType<WorkspaceSidebarView>().Single().IsVisible);
        Assert.Equal(!right, window.GetLogicalDescendants().OfType<SidePanelView>().Single().IsVisible);
        Assert.True(window.GetLogicalDescendants().OfType<MeetingRoomView>().Single().IsVisible);

        window.Close();
    }

    /// <summary>
    /// A collapsed sidebar is only reachable through the toolbar affordance, so that affordance may
    /// not be the part of the toolbar that scrolls out of sight when the window narrows.
    /// </summary>
    [AvaloniaFact]
    public void TheExpandAffordancesAppearOnCollapseAndSitOutsideTheScrollingToolbar()
    {
        var (window, vm, _) = Shown();
        var iconBar = window.GetLogicalDescendants().OfType<IconBarView>().Single();
        var buttons = iconBar.GetLogicalDescendants().OfType<Button>().ToList();
        var expandLeft = Assert.Single(buttons, button => button.Name == "ExpandWorkspace");
        var expandRight = Assert.Single(buttons, button => button.Name == "ExpandAssistant");
        var scroller = Assert.Single(
            iconBar.GetLogicalDescendants().OfType<ScrollViewer>(),
            view => view.HorizontalScrollBarVisibility == ScrollBarVisibility.Auto);

        Assert.False(expandLeft.IsVisible);
        Assert.False(expandRight.IsVisible);

        vm.Shell.ToggleLeftCommand.Execute(null);
        vm.Shell.ToggleRightCommand.Execute(null);

        Assert.True(expandLeft.IsVisible);
        Assert.True(expandRight.IsVisible);
        Assert.DoesNotContain(expandLeft, scroller.GetLogicalDescendants());
        Assert.DoesNotContain(expandRight, scroller.GetLogicalDescendants());

        window.Close();
    }

    [AvaloniaFact]
    public void EachSidebarCarriesItsOwnCollapseControl()
    {
        var (window, vm, _) = Shown();

        var workspace = window.GetLogicalDescendants().OfType<WorkspaceSidebarView>().Single();
        var assistant = window.GetLogicalDescendants().OfType<SidePanelView>().Single();

        Assert.Same(
            vm.Shell.ToggleLeftCommand,
            Assert.Single(
                workspace.GetLogicalDescendants().OfType<Button>(),
                button => button.Name == "CollapseWorkspace").Command);
        Assert.Same(
            vm.Shell.ToggleRightCommand,
            Assert.Single(
                assistant.GetLogicalDescendants().OfType<Button>(),
                button => button.Name == "CollapseAssistant").Command);

        window.Close();
    }

    /// <summary>Each sidebar's inner edge is the handle; neither is offered while it is collapsed.</summary>
    [AvaloniaFact]
    public void EachSidebarIsResizedFromItsInnerEdge()
    {
        var (window, vm, _) = Shown();
        var splitters = window.GetLogicalDescendants().OfType<GridSplitter>().ToList();
        var workspace = Assert.Single(splitters, s => s.Name == "WorkspaceSplitter");
        var assistant = Assert.Single(splitters, s => s.Name == "AssistantSplitter");

        Assert.Equal(1, Grid.GetColumn(workspace));
        Assert.Equal(3, Grid.GetColumn(assistant));
        Assert.True(workspace.IsVisible);

        vm.Shell.ToggleLeftCommand.Execute(null);

        Assert.False(workspace.IsVisible);
        Assert.True(assistant.IsVisible);

        window.Close();
    }
}
