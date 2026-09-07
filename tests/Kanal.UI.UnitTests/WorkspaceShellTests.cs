using Avalonia.Controls;
using Kanal.Host.ViewModels;

namespace Kanal.UI.UnitTests;

/// <summary>
/// The shell's whole job is that the transcript is never squeezed out. Collapsing a sidebar has to
/// give its width away and give it back, and no drag may take the centre below what a line of
/// three scripts needs.
/// </summary>
public class WorkspaceShellTests
{
    private static WorkspaceShellViewModel Shell() => new();

    [Fact]
    public void BothSidebarsStartOpenAtTheSameWidth()
    {
        var shell = Shell();

        Assert.False(shell.LeftCollapsed);
        Assert.False(shell.RightCollapsed);
        Assert.Equal(WorkspaceShellViewModel.DefaultWidth, shell.LeftWidth);
        Assert.Equal(WorkspaceShellViewModel.DefaultWidth, shell.RightWidth);
    }

    [Fact]
    public void CollapsingASidebarGivesItsColumnAwayEntirely()
    {
        var shell = Shell();

        shell.ToggleLeftCommand.Execute(null);

        Assert.True(shell.LeftCollapsed);
        Assert.Equal(new GridLength(0), shell.LeftColumn);
        Assert.Equal(new GridLength(WorkspaceShellViewModel.DefaultWidth), shell.RightColumn);
    }

    /// <summary>A width is a setting, not a side effect of the last thing that happened to it.</summary>
    [Fact]
    public void AChosenWidthSurvivesCollapseAndReExpansion()
    {
        var shell = Shell();
        shell.LeftWidth = 340;
        shell.RightWidth = 210;

        shell.ToggleLeftCommand.Execute(null);
        shell.ToggleRightCommand.Execute(null);
        shell.ToggleLeftCommand.Execute(null);
        shell.ToggleRightCommand.Execute(null);

        Assert.Equal(340, shell.LeftWidth);
        Assert.Equal(210, shell.RightWidth);
        Assert.Equal(new GridLength(340), shell.LeftColumn);
        Assert.Equal(new GridLength(210), shell.RightColumn);
    }

    [Theory]
    [InlineData(40, WorkspaceShellViewModel.MinWidth)]
    [InlineData(9000, WorkspaceShellViewModel.MaxWidth)]
    [InlineData(0, WorkspaceShellViewModel.MinWidth)]
    [InlineData(-100, WorkspaceShellViewModel.MinWidth)]
    public void ADragBeyondTheBoundedRangeStopsAtTheBound(double dragged, double expected)
    {
        var shell = Shell();

        shell.LeftWidth = dragged;
        shell.RightWidth = dragged;

        Assert.Equal(expected, shell.LeftWidth);
        Assert.Equal(expected, shell.RightWidth);
    }

    /// <summary>
    /// A splitter drag writes the column back, and a drag on a collapsed column would otherwise
    /// report zero and be clamped up to the minimum — quietly forgetting the width being kept.
    /// </summary>
    [Fact]
    public void AColumnWrittenBackWhileCollapsedDoesNotOverwriteTheKeptWidth()
    {
        var shell = Shell();
        shell.LeftWidth = 400;
        shell.ToggleLeftCommand.Execute(null);

        shell.LeftColumn = new GridLength(0);

        Assert.Equal(400, shell.LeftWidth);
        shell.ToggleLeftCommand.Execute(null);
        Assert.Equal(new GridLength(400), shell.LeftColumn);
    }

    [Fact]
    public void ASplitterDragIsAcceptedThroughTheColumn()
    {
        var shell = Shell();

        shell.LeftColumn = new GridLength(300);

        Assert.Equal(300, shell.LeftWidth);
    }

    /// <summary>The only way back from a collapsed sidebar is the affordance on the centre toolbar.</summary>
    [Fact]
    public void TheExpandAffordanceIsOfferedOnlyWhileTheSidebarIsCollapsed()
    {
        var shell = Shell();

        Assert.False(shell.CanExpandLeft);
        Assert.False(shell.CanExpandRight);

        shell.ToggleLeftCommand.Execute(null);

        Assert.True(shell.CanExpandLeft);
        Assert.False(shell.CanExpandRight);
    }

    /// <summary>
    /// The reserve is what a line of Chinese stacked against "wsporników" needs. Two sidebars at
    /// their widest plus the reserve is the narrowest window the shell can be laid out in.
    /// </summary>
    [Fact]
    public void TheTranscriptKeepsItsReserveWhateverTheSidebarsDo()
    {
        Assert.True(WorkspaceShellViewModel.TranscriptReserve > 0);
        Assert.True(WorkspaceShellViewModel.MinWidth < WorkspaceShellViewModel.MaxWidth);
        Assert.True(WorkspaceShellViewModel.DefaultWidth >= WorkspaceShellViewModel.MinWidth);
        Assert.True(WorkspaceShellViewModel.DefaultWidth <= WorkspaceShellViewModel.MaxWidth);
    }
}
