namespace Kanal.Host.ViewModels;

public class WorkspaceShellViewModel : ViewModelBase
{
    public const double TranscriptReserve = 320;

    public const double MinShellWidth =
        2 * (SidebarViewModel.MinWidth + SidebarViewModel.SplitterWidth) + TranscriptReserve;

    public SidebarViewModel Left { get; } = new();

    public SidebarViewModel Right { get; } = new();
}
