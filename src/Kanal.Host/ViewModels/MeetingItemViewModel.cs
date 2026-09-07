using System;
using System.IO;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Input;
using Kanal.Core.Workspaces;

namespace Kanal.Host.ViewModels;

public sealed partial class MeetingItemViewModel(
    MeetingRecord record,
    Func<MeetingItemViewModel, Task> import,
    Func<MeetingItemViewModel, Task> export) : ViewModelBase
{
    public MeetingRecord Record { get; } = record;

    public string Id => Record.Id;

    public string Title => Record.Title;

    public string When => Record.CreatedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm");

    public bool HasTranscript => File.Exists(Record.TranscriptPath);

    [RelayCommand]
    private Task Import() => import(this);

    [RelayCommand]
    private Task Export() => export(this);
}
