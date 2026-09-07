using System.ComponentModel;
using Avalonia.Controls;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Kanal.Host.ViewModels;

public partial class WorkspaceShellViewModel : ViewModelBase
{
    public const double TranscriptReserve = 320;
    public const double DefaultHeaderHeight = 76;
    public const double SplitterWidth = 5;

    // Pinned rather than Auto: the splitter's template measures a pixel wider than the handle, and
    // the window floor cannot account for a column whose width it does not set.
    public static GridLength SplitterColumn { get; } = new(SplitterWidth);

    // The toolbar reports its own height here and the two sidebar headers match it. A shared
    // constant only lines the rules up until a mode description wraps and the bar outgrows it.
    [ObservableProperty]
    private double _headerHeight = DefaultHeaderHeight;

    public SidebarViewModel Left { get; } = new();

    public SidebarViewModel Right { get; } = new();

    // What the window may not be dragged below. Computed rather than constant: two sidebars widened
    // to 480 need more room than two at 180, and a window narrower than they demand pushes the
    // right sidebar - and the only control that brings it back - off the screen entirely.
    public double MinShellWidth =>
        Demand(Left) + Demand(Right) + TranscriptReserve;

    public WorkspaceShellViewModel()
    {
        Left.PropertyChanged += OnSidebarChanged;
        Right.PropertyChanged += OnSidebarChanged;
    }

    private static double Demand(SidebarViewModel sidebar) =>
        sidebar.Collapsed ? 0 : sidebar.Width + SplitterWidth;

    private void OnSidebarChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(SidebarViewModel.Width) or nameof(SidebarViewModel.Collapsed))
            OnPropertyChanged(nameof(MinShellWidth));
    }
}
