using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kanal.Core.Diagnostics;
using Kanal.Core.Workspaces;
using Kanal.Host.Localization;

namespace Kanal.Host.ViewModels;

public sealed partial class WorkspaceSidebarViewModel : ViewModelBase
{
    private const string LogCategory = "workspace";

    private readonly WorkspaceStore _store;
    private readonly List<MeetingRecord> _held = [];

    public WorkspaceSidebarViewModel(WorkspaceStore store)
    {
        _store = store;
        Refresh();
    }

    public ObservableCollection<Workspace> Workspaces { get; } = new();

    public ObservableCollection<MeetingItemViewModel> Meetings { get; } = new();

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(NewMeetingCommand))]
    [NotifyCanExecuteChangedFor(nameof(ImportMeetingRecordCommand))]
    private Workspace? _selectedWorkspace;

    [ObservableProperty]
    private MeetingItemViewModel? _selectedMeeting;

    [ObservableProperty]
    private string _search = "";

    [ObservableProperty]
    private string _problemNote = "";

    public Func<Task<string?>>? ChooseWorkspaceFolder { get; set; }

    public Func<Task<string?>>? ChooseFileToImport { get; set; }

    public Func<string, Task<string?>>? ChooseExportPath { get; set; }

    public bool HasWorkspace => SelectedWorkspace is not null;

    public string EmptyNote =>
        Search.Trim().Length > 0 ? L["workspace.nomatches"] : L["workspace.nomeetings"];

    public bool RenameSelectedMeeting(string title)
    {
        if (SelectedWorkspace is not { } workspace || SelectedMeeting is not { } meeting)
            return false;

        var (renamed, problem) = _store.RenameMeeting(workspace.Id, meeting.Id, title);
        if (Refused(problem) || renamed is null)
            return false;

        LoadMeetings([]);
        SelectedMeeting = Meetings.FirstOrDefault(m => m.Id == renamed.Id);
        return true;
    }

    public void Refresh()
    {
        var listing = _store.ListWorkspaces();
        var keep = SelectedWorkspace?.Id;
        Workspaces.Clear();
        foreach (var workspace in listing.Workspaces)
            Workspaces.Add(workspace);

        SelectedWorkspace = Workspaces.FirstOrDefault(w => w.Id == keep) ?? Workspaces.FirstOrDefault();
        LoadMeetings(listing.Problems);
    }

    partial void OnSelectedWorkspaceChanged(Workspace? value)
    {
        OnPropertyChanged(nameof(HasWorkspace));
        LoadMeetings([]);
    }

    partial void OnSearchChanged(string value) => Show();

    private void LoadMeetings(IReadOnlyList<StoreProblem> carried)
    {
        _held.Clear();
        var problems = carried.ToList();
        if (SelectedWorkspace is { } workspace)
        {
            var listing = _store.ListMeetings(workspace.Id);
            _held.AddRange(listing.Meetings.OrderByDescending(m => m.CreatedAt));
            problems.AddRange(listing.Problems);
        }

        ProblemNote = problems.Count == 0 ? "" : L.Format("workspace.problems", problems.Count);
        Show();
    }

    private void Show()
    {
        OnPropertyChanged(nameof(EmptyNote));
        var keep = SelectedMeeting?.Id;
        Meetings.Clear();
        foreach (var record in _held.Where(Matches))
            Meetings.Add(new MeetingItemViewModel(record, ImportIntoAsync, ExportAsync));

        SelectedMeeting = Meetings.FirstOrDefault(m => m.Id == keep);
    }

    private bool Matches(MeetingRecord record) =>
        Search.Trim() is var text && (text.Length == 0 ||
            record.Title.Contains(text, StringComparison.CurrentCultureIgnoreCase));

    [RelayCommand(CanExecute = nameof(HasWorkspace))]
    private void NewMeeting()
    {
        var (meeting, problem) = _store.CreateMeeting(SelectedWorkspace!.Id, L["meeting.untitled"]);
        if (Refused(problem))
            return;

        Search = "";
        LoadMeetings([]);
        SelectedMeeting = Meetings.FirstOrDefault(m => m.Id == meeting!.Id);
    }

    [RelayCommand]
    private async Task NewProjectAsync()
    {
        if (ChooseWorkspaceFolder is null || await ChooseWorkspaceFolder() is not { } folder)
            return;

        var (workspace, problem) = _store.CreateWorkspace(
            Path.GetFileName(folder.TrimEnd(Path.DirectorySeparatorChar)), folder);
        if (Refused(problem))
            return;

        Refresh();
        SelectedWorkspace = Workspaces.FirstOrDefault(w => w.Id == workspace!.Id);
    }

    [RelayCommand(CanExecute = nameof(HasWorkspace))]
    private async Task ImportMeetingRecordAsync()
    {
        if (ChooseFileToImport is null || await ChooseFileToImport() is not { } source)
            return;

        var (meeting, problem) = _store.CreateMeeting(
            SelectedWorkspace!.Id, Path.GetFileNameWithoutExtension(source));
        if (Refused(problem))
            return;

        await CopyIntoAsync(meeting!, source);
    }

    private async Task ImportIntoAsync(MeetingItemViewModel item)
    {
        if (ChooseFileToImport is null || await ChooseFileToImport() is not { } source)
            return;

        await CopyIntoAsync(item.Record, source);
    }

    private Task CopyIntoAsync(MeetingRecord meeting, string source)
    {
        var folder = _store.MeetingFolder(meeting.WorkspaceId, meeting.Id);
        if (folder is null)
            return Task.CompletedTask;

        var target = Path.Combine(folder, Path.GetFileName(source));
        try
        {
            Directory.CreateDirectory(folder);
            File.Copy(source, target, overwrite: true);
        }
        catch (Exception ex)
        {
            ProblemNote = L.Format("workspace.importfailed", ex.Message);
            Log.Error(LogCategory, $"{source} could not be brought into {folder}.", ex);
            return Task.CompletedTask;
        }

        Refused(_store.SaveMeeting(meeting with { TranscriptPath = target }).Problem);
        LoadMeetings([]);
        return Task.CompletedTask;
    }

    private async Task ExportAsync(MeetingItemViewModel item)
    {
        if (item.Record.TranscriptPath is not { } transcript || !File.Exists(transcript))
        {
            ProblemNote = L["workspace.nothingtoexport"];
            return;
        }

        if (ChooseExportPath is null || await ChooseExportPath(Path.GetFileName(transcript)) is not { } target)
            return;

        try
        {
            File.Copy(transcript, target, overwrite: true);
            ProblemNote = "";
        }
        catch (Exception ex)
        {
            ProblemNote = L.Format("workspace.exportfailed", ex.Message);
            Log.Error(LogCategory, $"{transcript} could not be written to {target}.", ex);
        }
    }

    private bool Refused(StoreProblem? problem)
    {
        if (problem is null)
            return false;

        ProblemNote = problem.Detail;
        Log.Warning(LogCategory, $"{problem.Kind} on {problem.Subject}: {problem.Detail}");
        return true;
    }

    private static Localizer L => Localizer.Instance;
}
