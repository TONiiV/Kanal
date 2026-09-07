using System;
using Avalonia.Controls;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Kanal.Host.ViewModels;

public partial class WorkspaceShellViewModel : ViewModelBase
{
    public const double MinWidth = 180;
    public const double MaxWidth = 480;
    public const double DefaultWidth = 272;

    // A floor the grid enforces, not a preference: the transcript sets a CJK line against
    // "wsporników" and cannot be the column that absorbs both sidebars.
    public const double TranscriptReserve = 320;

    private double _leftWidth = DefaultWidth;
    private double _rightWidth = DefaultWidth;

    public double LeftWidth
    {
        get => _leftWidth;
        set => SetWidth(ref _leftWidth, value, nameof(LeftWidth), nameof(LeftColumn), LeftCollapsed);
    }

    public double RightWidth
    {
        get => _rightWidth;
        set => SetWidth(ref _rightWidth, value, nameof(RightWidth), nameof(RightColumn), RightCollapsed);
    }

    // Two-way bound to the grid, so a splitter drag arrives here as a column and a collapse leaves
    // as one. A collapsed column is written back as zero, which is not a width anyone chose.
    public GridLength LeftColumn
    {
        get => Column(LeftCollapsed, _leftWidth);
        set => LeftWidth = value.Value;
    }

    public GridLength RightColumn
    {
        get => Column(RightCollapsed, _rightWidth);
        set => RightWidth = value.Value;
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LeftColumn))]
    [NotifyPropertyChangedFor(nameof(CanExpandLeft))]
    private bool _leftCollapsed;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RightColumn))]
    [NotifyPropertyChangedFor(nameof(CanExpandRight))]
    private bool _rightCollapsed;

    public bool CanExpandLeft => LeftCollapsed;

    public bool CanExpandRight => RightCollapsed;

    [RelayCommand]
    private void ToggleLeft() => LeftCollapsed = !LeftCollapsed;

    [RelayCommand]
    private void ToggleRight() => RightCollapsed = !RightCollapsed;

    private static GridLength Column(bool collapsed, double width) =>
        new(collapsed ? 0 : width);

    private void SetWidth(ref double field, double value, string width, string column, bool collapsed)
    {
        // A collapsed column reads back as zero on every layout pass. Taking that as a drag would
        // clamp it up to the minimum and lose the width the sidebar is meant to come back to.
        if (collapsed && value <= 0)
            return;

        var bounded = Math.Clamp(value, MinWidth, MaxWidth);
        if (Math.Abs(field - bounded) < 0.5)
            return;

        field = bounded;
        OnPropertyChanged(width);
        OnPropertyChanged(column);
    }
}
