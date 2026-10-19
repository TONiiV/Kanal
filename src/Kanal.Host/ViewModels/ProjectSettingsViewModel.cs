using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kanal.Core.Workspaces;
using Kanal.Host.Controls;
using Kanal.Host.Localization;

namespace Kanal.Host.ViewModels;

public enum ProjectRemoval
{
    ListOnly,

    DeleteFiles,
}

public sealed partial class ProjectSettingsViewModel : ViewModelBase
{
    private const string LogCategory = "workspace";

    private readonly WorkspaceStore _store;
    private readonly IReadOnlyList<GlossaryEntry> _savedGlossary;

    public ProjectSettingsViewModel(
        WorkspaceStore store,
        Workspace project,
        bool recording,
        IReadOnlyList<ProjectIssueViewModel>? issues = null,
        bool openAtIssues = false,
        bool projectRecording = false)
    {
        _store = store;
        Project = project;
        CanResolve = !projectRecording;
        Issues = [.. (issues ?? []).Select(Offered)];
        OpenAtIssues = openAtIssues && Issues.Count > 0;
        var glossary = store.ReadGlossary(project.Id);
        _savedGlossary = glossary.Entries ?? [];
        Glossary = [.. _savedGlossary.Select(entry => new GlossaryRowViewModel(entry))];
        CanEditGlossary = glossary.Problem is null;
        GlossaryProblem = glossary.Problem is { } problem ? StoreRefusal.Report(problem, LogCategory) : "";
        _name = project.Name;
        _glyph = project.IconGlyph;
        _imagePath = project.IconPath;
        CanRemove = !recording;
    }

    public Workspace Project { get; private set; }

    public IReadOnlyList<string> Glyphs => ProjectIcons.Glyphs;

    public ObservableCollection<ProjectIssueViewModel> Issues { get; }

    public ObservableCollection<GlossaryRowViewModel> Glossary { get; }

    public string GlossaryProblem { get; }

    public bool CanEditGlossary { get; }

    [RelayCommand(CanExecute = nameof(CanEditGlossary))]
    private void AddTerm() => Glossary.Add(new GlossaryRowViewModel());

    [RelayCommand(CanExecute = nameof(CanEditGlossary))]
    private void RemoveTerm(GlossaryRowViewModel row) => Glossary.Remove(row);

    public bool HasIssues => Issues.Count > 0;

    public bool OpenAtIssues { get; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    private string _name;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCustomIcon))]
    private string? _glyph;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCustomIcon))]
    [NotifyPropertyChangedFor(nameof(ImageFileName))]
    private string? _imagePath;

    [ObservableProperty]
    private string _problem = "";

    public bool IsCustomIcon => Glyph is not null || ImagePath is not null;

    public string? ImageFileName => ImagePath is null ? null : Path.GetFileName(ImagePath);

    public bool CanRemove { get; }

    public string RemoveBlockedNote => CanRemove ? "" : L["workspace.remove.recording"];

    public bool CanResolve { get; }

    public string ResolveBlockedNote => CanResolve ? "" : L["workspace.issues.recording"];

    public bool Changed { get; private set; }

    public Func<IReadOnlyList<ProjectIssueViewModel>>? ReloadIssues { get; set; }

    public Func<Task<string?>>? ChooseProjectFolder { get; set; }

    public Func<string, Task<bool>>? ConfirmListRemoval { get; set; }

    public Func<Task<string?>>? ChooseImageFile { get; set; }

    public Func<string, Task<ProjectRemoval?>>? ConfirmRemoval { get; set; }

    public event Action? Finished;

    [RelayCommand]
    private void ChooseGlyph(string glyph)
    {
        Glyph = glyph;
        ImagePath = null;
    }

    [RelayCommand]
    private async Task ChooseImageAsync()
    {
        if (ChooseImageFile is null || await ChooseImageFile() is not { } path)
            return;

        if (new FileInfo(path) is { Exists: true, Length: > WorkspaceStore.MaxIconBytes })
        {
            Problem = L["workspace.icon.toolarge"];
            return;
        }

        Problem = "";
        ImagePath = path;
        Glyph = null;
    }

    [RelayCommand]
    private void ResetIcon()
    {
        Glyph = null;
        ImagePath = null;
    }

    private bool CanSave() => !string.IsNullOrWhiteSpace(Name);

    [RelayCommand(CanExecute = nameof(CanSave))]
    private void Save()
    {
        var glossary = Glossary.Select(row => row.ToEntry()).ToList();
        if (CanEditGlossary && !glossary.SequenceEqual(_savedGlossary)
            && Refused(_store.SaveGlossary(Project.Id, glossary)))
            return;

        var icon = ImagePath is not null && ImagePath != Project.IconPath
                ? _store.SetWorkspaceIconFile(Project.Id, ImagePath)
            : ImagePath is null && Glyph is not null && Glyph != Project.IconGlyph
                ? _store.SetWorkspaceIcon(Project.Id, Glyph)
            : !IsCustomIcon && (Project.IconGlyph is not null || Project.IconFile is not null)
                ? _store.ResetWorkspaceIcon(Project.Id)
            : null;

        if (Refused(icon?.Problem))
            return;

        Changed |= icon is not null;
        if (Name.Trim() != Project.Name)
        {
            if (Refused(_store.RenameWorkspace(Project.Id, Name).Problem))
                return;

            Changed = true;
        }

        Finished?.Invoke();
    }

    [RelayCommand(CanExecute = nameof(CanRemove))]
    private async Task RemoveAsync()
    {
        if (!CanRemove || ConfirmRemoval is null || await ConfirmRemoval(Project.Name) is not { } removal)
            return;

        var problem = removal == ProjectRemoval.DeleteFiles
            ? _store.DeleteWorkspace(Project.Id)
            : _store.ForgetWorkspace(Project.Id);
        if (Refused(problem))
            return;

        Changed = true;
        Finished?.Invoke();
    }

    [RelayCommand(CanExecute = nameof(CanResolve))]
    private void Resolve(ProjectIssueViewModel issue)
    {
        if (!CanResolve || !Actionable(issue))
            return;

        switch (issue.Action)
        {
            case IssueAction.RecoverMeeting:
                Show(issue, _store.RecoverMeeting(Project.Id, issue.Subject, RecoveredTitle), "workspace.issues.recovered");
                break;
            case IssueAction.ReidentifyMeeting:
                Show(issue, _store.ReidentifyMeeting(Project.Id, issue.Subject), "workspace.issues.reidentified");
                break;
            case IssueAction.CreateMeetingsFolder:
                Show(issue, _store.CreateMeetingsFolder(Project.Id), L["workspace.issues.meetingscreated"]);
                break;
            case IssueAction.DropBrokenRows:
                Show(issue, _store.DropBrokenRegistryRows(), L["workspace.issues.rowsremoved"]);
                break;
            default:
                return;
        }

        Reload();
    }

    [RelayCommand(CanExecute = nameof(CanResolve))]
    private async Task RelocateAsync(ProjectIssueViewModel issue)
    {
        if (!CanResolve || !Actionable(issue) || issue.Action != IssueAction.RelocateProject
            || ChooseProjectFolder is null || await ChooseProjectFolder() is not { } folder)
            return;

        var (oldRoot, oldIcon) = (Project.RootPath, Project.IconPath);
        var (moved, problem) = _store.RelocateWorkspace(Project.Id, folder);
        if (problem is null)
        {
            Project = moved!;
            if (ImagePath is not null && ImagePath == oldIcon)
                ImagePath = moved!.IconPath;
            issue.Succeeded(L.Format("workspace.issues.relocated", moved!.RootPath));
        }
        else
        {
            issue.Refused(ReasonFor(problem, oldRoot));
        }

        Reload();
    }

    public bool CanRemoveFromList => CanResolve && CanRemove;

    [RelayCommand(CanExecute = nameof(CanRemoveFromList))]
    private async Task RemoveFromListAsync(ProjectIssueViewModel issue)
    {
        if (!CanRemoveFromList || !Actionable(issue) || issue.Action != IssueAction.RelocateProject
            || ConfirmListRemoval is null || !await ConfirmListRemoval(Project.Name))
            return;

        if (_store.ForgetWorkspace(Project.Id) is { } problem)
        {
            issue.Refused(ReasonFor(problem));
            Reload();
            return;
        }

        Changed = true;
        Finished?.Invoke();
    }

    private static bool Actionable(ProjectIssueViewModel issue) => !issue.IsResolved && issue.ActionsAllowed;

    private static string RecoveredTitle(DateTimeOffset start) => L.Format(
        "workspace.issues.recoveredtitle",
        start.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture));

    private ProjectIssueViewModel Offered(ProjectIssueViewModel issue)
    {
        issue.ActionsAllowed = CanResolve;
        return issue;
    }

    private void Show(ProjectIssueViewModel issue, RecoverResult result, string recoveredKey)
    {
        if (result.Problem is { } problem)
            issue.Refused(ReasonFor(problem));
        else if (result.SetAsidePath is { } aside)
            issue.Succeeded(L.Format("workspace.issues.setaside", Path.GetRelativePath(Project.RootPath, aside)));
        else
            issue.Succeeded(L.Format(recoveredKey, result.Recovered!.Title));
    }

    private void Show(ProjectIssueViewModel issue, StoreProblem? problem, string outcome)
    {
        if (problem is null)
            issue.Succeeded(outcome);
        else
            issue.Refused(ReasonFor(problem));
    }

    private static string ReasonFor(StoreProblem problem, string? oldRoot = null) =>
        StoreRefusal.Report(problem, LogCategory, problem.Kind switch
        {
            StoreProblemKind.FolderMissing or StoreProblemKind.NotFound => "workspace.issues.refused.missing",
            StoreProblemKind.Unwritable => "workspace.issues.refused.unwritable",
            StoreProblemKind.NotThisWorkspace => "workspace.issues.refused.otherproject",
            StoreProblemKind.Invalid when oldRoot is not null && Directory.Exists(oldRoot) =>
                "workspace.issues.refused.oldfolder",
            StoreProblemKind.Invalid when oldRoot is not null => "workspace.issues.refused.taken",
            _ => "workspace.issues.refused.other",
        });

    private void Reload()
    {
        if (ReloadIssues is null)
            return;

        var fresh = ReloadIssues();
        for (var i = Issues.Count - 1; i >= 0; i--)
        {
            if (fresh.Any(f => f.Problem == Issues[i].Problem))
                continue;
            if (Issues[i].HasOutcome)
                Issues[i].ActionsAllowed = false;
            else
                Issues.RemoveAt(i);
        }

        foreach (var issue in fresh.Where(f => !Issues.Any(i => !i.IsResolved && i.Problem == f.Problem)))
            Issues.Add(Offered(issue));
        OnPropertyChanged(nameof(HasIssues));
    }

    private bool Refused(StoreProblem? problem)
    {
        if (problem is null)
            return false;

        Problem = StoreRefusal.Report(
            problem,
            LogCategory,
            problem.Kind == StoreProblemKind.NotFound && problem.Subject == ImagePath ? "workspace.icon.missing" : null);
        return true;
    }

    private static Localizer L => Localizer.Instance;
}
