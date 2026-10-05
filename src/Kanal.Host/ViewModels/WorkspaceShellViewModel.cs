using System;
using System.ComponentModel;
using Avalonia.Controls;

namespace Kanal.Host.ViewModels;

// Reserved by the headers because the platform draws its own window controls over the client area.
public readonly record struct WindowControlInsets(double Start, double End)
{
    public static WindowControlInsets None { get; } = new(0, 0);

    // Traffic lights on the left on macOS; three 46 DIP caption buttons on the right elsewhere.
    public static WindowControlInsets ForCurrentPlatform() =>
        OperatingSystem.IsMacOS() ? new(78, 0) : new(0, 138);
}

public partial class WorkspaceShellViewModel : ViewModelBase
{
    public const double TranscriptReserve = 320;
    public const double HeaderHeight = 58;
    public const double SplitterWidth = 5;

    // Pinned rather than Auto: the splitter's template measures a pixel wider than the handle, and
    // the window floor cannot account for a column whose width it does not set.
    public static GridLength SplitterColumn { get; } = new(SplitterWidth);

    private readonly WindowControlInsets _insets;

    public SidebarViewModel Left { get; } = new();

    public SidebarViewModel Right { get; } = new();

    // What the window may not be dragged below. Computed rather than constant: two sidebars widened
    // to 480 need more room than two at 180, and a window narrower than they demand pushes the
    // right sidebar - and the only control that brings it back - off the screen entirely.
    public double MinShellWidth =>
        Demand(Left) + Demand(Right) + TranscriptReserve;

    public double WorkspaceStartInset => Left.Collapsed ? 0 : _insets.Start;

    public double CenterStartInset => Left.Collapsed ? _insets.Start : 0;

    public double CenterEndInset => Right.Collapsed ? _insets.End : 0;

    public double AssistantEndInset => Right.Collapsed ? 0 : _insets.End;

    public WorkspaceShellViewModel(WindowControlInsets? insets = null)
    {
        _insets = insets ?? WindowControlInsets.None;
        Left.PropertyChanged += OnSidebarChanged;
        Right.PropertyChanged += OnSidebarChanged;
    }

    private static double Demand(SidebarViewModel sidebar) =>
        sidebar.Collapsed ? 0 : sidebar.Width + SplitterWidth;

    private void OnSidebarChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(SidebarViewModel.Width) or nameof(SidebarViewModel.Collapsed))
            OnPropertyChanged(nameof(MinShellWidth));

        if (e.PropertyName is nameof(SidebarViewModel.Collapsed))
        {
            OnPropertyChanged(nameof(WorkspaceStartInset));
            OnPropertyChanged(nameof(CenterStartInset));
            OnPropertyChanged(nameof(CenterEndInset));
            OnPropertyChanged(nameof(AssistantEndInset));
        }
    }
}
