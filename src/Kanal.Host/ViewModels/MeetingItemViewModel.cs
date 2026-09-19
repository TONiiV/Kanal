using System;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kanal.Core.Workspaces;
using Kanal.Host.Localization;

namespace Kanal.Host.ViewModels;

public sealed partial class MeetingItemViewModel(
    MeetingRecord record,
    Func<MeetingItemViewModel, string, bool> rename,
    Func<MeetingItemViewModel, Task> generateTitle,
    Func<MeetingItemViewModel, Task> import,
    Func<MeetingItemViewModel, Task> export,
    Func<MeetingItemViewModel, Task> exportBundle,
    Func<MeetingItemViewModel, Task> openFolder,
    Func<MeetingItemViewModel, Task> delete) : ViewModelBase
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

    [ObservableProperty]
    private bool _isRenaming;

    [ObservableProperty]
    private string _draft = "";

    [RelayCommand]
    private void BeginRename()
    {
        Draft = Title;
        IsRenaming = true;
    }

    [RelayCommand]
    private void CommitRename()
    {
        if (!IsRenaming)
            return;

        IsRenaming = false;
        rename(this, Draft);
    }

    [RelayCommand]
    private void CancelRename() => IsRenaming = false;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(GenerateTitleCommand))]
    private bool _canGenerateTitle;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(GenerateTitleHeader))]
    private string? _namingBlockedBy;

    [ObservableProperty]
    private bool _isNaming;

    // A disabled MenuItem never opens its tooltip, so the header itself says why.
    public string GenerateTitleHeader => Localizer.Instance[NamingBlockedBy ?? "meeting.generatetitle"];

    [RelayCommand(CanExecute = nameof(CanGenerateTitle))]
    private Task GenerateTitle() => generateTitle(this);

    public void OnLanguageChanged() => OnPropertyChanged(nameof(GenerateTitleHeader));

    [RelayCommand]
    private Task Import() => import(this);

    [RelayCommand]
    private Task Export() => export(this);

    [RelayCommand]
    private Task ExportBundle() => exportBundle(this);

    [RelayCommand]
    private Task OpenFolder() => openFolder(this);

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(DeleteCommand))]
    private bool _isRecording;

    public bool CanDelete => !IsRecording;

    [RelayCommand(CanExecute = nameof(CanDelete))]
    private Task Delete() => delete(this);
}
