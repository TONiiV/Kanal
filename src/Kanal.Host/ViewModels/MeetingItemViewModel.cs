using System;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Input;
using Kanal.Core.Workspaces;

namespace Kanal.Host.ViewModels;

public sealed partial class MeetingItemViewModel(
    MeetingRecord record,
    Func<MeetingItemViewModel, Task> import,
    Func<MeetingItemViewModel, Task> export) : ViewModelBase
{
    public MeetingRecord Record { get; private set; } = record;

    // Renaming updates the item rather than rebuilding the list: a fresh instance would read as
    // the operator selecting a different meeting, and the sidebar would reset around it.
    public void Adopt(MeetingRecord renamed)
    {
        Record = renamed;
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(When));
    }

    public string Id => Record.Id;

    public string Title => Record.Title;

    public string When => Record.CreatedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm");

    [RelayCommand]
    private Task Import() => import(this);

    [RelayCommand]
    private Task Export() => export(this);
}
