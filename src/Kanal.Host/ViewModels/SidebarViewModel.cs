using System;
using Avalonia.Controls;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Kanal.Host.ViewModels;

public partial class SidebarViewModel : ViewModelBase
{
    public const double MinWidth = 180;
    public const double MaxWidth = 480;
    public const double DefaultWidth = 272;

    private double _width = DefaultWidth;

    public double Width
    {
        get => _width;
        set
        {
            // A collapsed column reads back as zero on every layout pass. Taking that as a drag
            // would clamp it up to the minimum and lose the width the sidebar comes back to.
            if (Collapsed && value <= 0)
                return;

            var bounded = Math.Clamp(value, MinWidth, MaxWidth);
            if (Math.Abs(_width - bounded) < 0.5)
                return;

            _width = bounded;
            OnPropertyChanged();
            OnPropertyChanged(nameof(Column));
        }
    }

    // Two-way bound to the grid, so a splitter drag arrives as a column and a collapse leaves as one.
    public GridLength Column
    {
        get => new(Collapsed ? 0 : _width);
        set => Width = value.Value;
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Column))]
    [NotifyPropertyChangedFor(nameof(CanExpand))]
    [NotifyPropertyChangedFor(nameof(ColumnMinWidth))]
    private bool _collapsed;

    public bool CanExpand => Collapsed;

    // The splitter reads its drag limits off the column definition rather than off this width, so
    // the bounds have to be published to the column too - and give way while the sidebar is gone.
    public double ColumnMinWidth => Collapsed ? 0 : MinWidth;

    public double ColumnMaxWidth => MaxWidth;

    [RelayCommand]
    private void Toggle() => Collapsed = !Collapsed;
}
