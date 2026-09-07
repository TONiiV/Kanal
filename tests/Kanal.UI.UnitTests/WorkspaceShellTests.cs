using Avalonia.Controls;
using Kanal.Host.ViewModels;

namespace Kanal.UI.UnitTests;

public class WorkspaceShellTests
{
    private static WorkspaceShellViewModel Shell() => new();

    [Fact]
    public void BothSidebarsStartOpenAtTheSameWidth()
    {
        var shell = Shell();

        Assert.False(shell.Left.Collapsed);
        Assert.False(shell.Right.Collapsed);
        Assert.Equal(SidebarViewModel.DefaultWidth, shell.Left.Width);
        Assert.Equal(SidebarViewModel.DefaultWidth, shell.Right.Width);
    }

    [Fact]
    public void CollapsingASidebarGivesItsColumnAwayEntirelyAndLeavesTheOther()
    {
        var shell = Shell();

        shell.Left.ToggleCommand.Execute(null);

        Assert.True(shell.Left.Collapsed);
        Assert.Equal(new GridLength(0), shell.Left.Column);
        Assert.False(shell.Right.Collapsed);
        Assert.Equal(new GridLength(SidebarViewModel.DefaultWidth), shell.Right.Column);
    }

    [Fact]
    public void AChosenWidthSurvivesCollapseAndReExpansion()
    {
        var shell = Shell();
        shell.Left.Width = 340;
        shell.Right.Width = 210;

        shell.Left.ToggleCommand.Execute(null);
        shell.Right.ToggleCommand.Execute(null);
        shell.Left.ToggleCommand.Execute(null);
        shell.Right.ToggleCommand.Execute(null);

        Assert.Equal(340, shell.Left.Width);
        Assert.Equal(210, shell.Right.Width);
        Assert.Equal(new GridLength(340), shell.Left.Column);
        Assert.Equal(new GridLength(210), shell.Right.Column);
    }

    [Theory]
    [InlineData(40, SidebarViewModel.MinWidth)]
    [InlineData(9000, SidebarViewModel.MaxWidth)]
    [InlineData(0, SidebarViewModel.MinWidth)]
    [InlineData(-100, SidebarViewModel.MinWidth)]
    public void ADragBeyondTheBoundedRangeStopsAtTheBound(double dragged, double expected)
    {
        var sidebar = new SidebarViewModel { Width = dragged };

        Assert.Equal(expected, sidebar.Width);
    }

    [Fact]
    public void AColumnWrittenBackWhileCollapsedDoesNotOverwriteTheKeptWidth()
    {
        var sidebar = new SidebarViewModel { Width = 400 };
        sidebar.ToggleCommand.Execute(null);

        sidebar.Column = new GridLength(0);

        Assert.Equal(400, sidebar.Width);
        sidebar.ToggleCommand.Execute(null);
        Assert.Equal(new GridLength(400), sidebar.Column);
    }

    [Fact]
    public void ASplitterDragIsAcceptedThroughTheColumn()
    {
        var sidebar = new SidebarViewModel();

        sidebar.Column = new GridLength(300);

        Assert.Equal(300, sidebar.Width);
    }

    [Fact]
    public void TheExpandAffordanceIsOfferedOnlyWhileTheSidebarIsCollapsed()
    {
        var shell = Shell();

        Assert.False(shell.Left.CanExpand);
        Assert.False(shell.Right.CanExpand);

        shell.Left.ToggleCommand.Execute(null);

        Assert.True(shell.Left.CanExpand);
        Assert.False(shell.Right.CanExpand);
    }

    /// <summary>
    /// A sidebar dragged to 480 needs more window than one at 180. A fixed floor let the operator
    /// shrink the window until the right sidebar - and the only control that reopens it - was off
    /// the screen.
    /// </summary>
    [Fact]
    public void TheWindowFloorFollowsTheWidthsTheSidebarsCurrentlyHold()
    {
        var shell = Shell();
        var atDefault = shell.MinShellWidth;

        shell.Left.Width = SidebarViewModel.MaxWidth;

        Assert.Equal(atDefault + (SidebarViewModel.MaxWidth - SidebarViewModel.DefaultWidth),
            shell.MinShellWidth);

        shell.Left.ToggleCommand.Execute(null);

        Assert.Equal(
            SidebarViewModel.DefaultWidth + WorkspaceShellViewModel.SplitterWidth
                + WorkspaceShellViewModel.TranscriptReserve,
            shell.MinShellWidth);
    }

    /// <summary>
    /// The splitter reads its drag limits off the column definition, not off this view model, so
    /// the bounds have to reach the column - and give way entirely while the sidebar is collapsed.
    /// </summary>
    [Fact]
    public void TheColumnCarriesTheSameBoundsTheSplitterHasToRespect()
    {
        var sidebar = new SidebarViewModel();

        Assert.Equal(SidebarViewModel.MinWidth, sidebar.ColumnMinWidth);
        Assert.Equal(SidebarViewModel.MaxWidth, sidebar.ColumnMaxWidth);

        sidebar.ToggleCommand.Execute(null);

        // Only the floor gives way. A zero ceiling would make re-expansion depend on which of the
        // two notifications the grid happened to read first.
        Assert.Equal(0, sidebar.ColumnMinWidth);
        Assert.Equal(SidebarViewModel.MaxWidth, sidebar.ColumnMaxWidth);
    }

    [Fact]
    public void TheTwoSidebarsCarryTheirOwnStateRatherThanSharingIt()
    {
        var shell = Shell();

        shell.Left.Width = 400;
        shell.Right.ToggleCommand.Execute(null);

        Assert.Equal(SidebarViewModel.DefaultWidth, shell.Right.Width);
        Assert.False(shell.Left.Collapsed);
    }
}
