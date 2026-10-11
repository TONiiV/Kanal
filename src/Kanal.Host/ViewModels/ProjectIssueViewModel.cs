using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using Kanal.Core.Workspaces;
using Kanal.Host.Localization;

namespace Kanal.Host.ViewModels;

public enum IssueOrigin
{
    Project,

    ProjectListRow,

    ProjectList,
}

public enum IssueAction
{
    None,

    RecoverMeeting,

    ReidentifyMeeting,

    RelocateProject,

    CreateMeetingsFolder,

    DropBrokenRows,
}

public sealed partial class ProjectIssueViewModel : ViewModelBase
{
    private readonly string _causeKey;
    private readonly string? _projectName;

    private ProjectIssueViewModel(
        StoreProblem problem, string causeKey, string? projectName, IssueAction action, bool alarm = true)
    {
        Problem = problem;
        _causeKey = causeKey;
        _projectName = projectName;
        Action = action;
        IsAlarm = alarm;
    }

    public StoreProblem Problem { get; }

    public StoreProblemKind Kind => Problem.Kind;

    public string Subject => Problem.Subject;

    public string Location =>
        Path.GetFileName(Subject) == WorkspaceStore.MeetingFileName ? Path.GetDirectoryName(Subject)! : Subject;

    public bool IsAlarm { get; }

    public string Cause => _projectName is null ? L[_causeKey] : L.Format(_causeKey, _projectName);

    public IssueAction Action { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowsResolve))]
    [NotifyPropertyChangedFor(nameof(ShowsRelocate))]
    private bool _actionsAllowed = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowsResolve))]
    [NotifyPropertyChangedFor(nameof(ShowsRelocate))]
    private bool _isResolved;

    [ObservableProperty]
    private bool _isRefused;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasOutcome))]
    private string _outcome = "";

    public bool HasOutcome => Outcome.Length > 0;

    public bool ShowsResolve => ActionsAllowed && !IsResolved
        && Action is not (IssueAction.None or IssueAction.RelocateProject);

    public bool ShowsRelocate => ActionsAllowed && !IsResolved && Action == IssueAction.RelocateProject;

    internal void Succeeded(string outcome)
    {
        IsRefused = false;
        IsResolved = true;
        Outcome = outcome;
    }

    internal void Refused(string reason)
    {
        IsRefused = true;
        Outcome = reason;
    }

    public static ProjectIssueViewModel For(StoreProblem problem, Workspace? owner, IssueOrigin origin = IssueOrigin.Project)
    {
        if (problem.Kind == StoreProblemKind.RegistryRowDamaged)
            return new(problem, "workspace.issues.projectlist", null, IssueAction.DropBrokenRows);
        if (origin == IssueOrigin.ProjectList)
            return new(problem, "workspace.issues.projectlistunreadable", null, IssueAction.None);
        if (origin == IssueOrigin.ProjectListRow)
            return new(problem, "workspace.issues.projectlist", null, IssueAction.None);

        var meetings = owner is null ? null : Path.Combine(owner.RootPath, WorkspaceStore.MeetingsFolderName);
        return problem.Kind switch
        {
            StoreProblemKind.FolderMissing when owner is not null && problem.Subject == owner.RootPath =>
                new(problem, "workspace.issues.projectmissing", owner.Name, IssueAction.RelocateProject, alarm: false),
            StoreProblemKind.FolderMissing when problem.Subject == meetings =>
                new(problem, "workspace.issues.meetingsmissing", null, IssueAction.CreateMeetingsFolder, alarm: false),
            StoreProblemKind.FolderMissing =>
                new(problem, "workspace.issues.foldermissing", null, IssueAction.None, alarm: false),
            StoreProblemKind.UnsupportedVersion => new(problem, "workspace.issues.format", null, IssueAction.None),
            StoreProblemKind.RecordDamaged =>
                new(problem, "workspace.issues.damaged", null, IssueAction.RecoverMeeting),
            StoreProblemKind.RecordMissing =>
                new(problem, "workspace.issues.norecord", null, IssueAction.RecoverMeeting),
            StoreProblemKind.Misfiled =>
                new(problem, "workspace.issues.misfiled", null, IssueAction.ReidentifyMeeting),
            StoreProblemKind.Unreadable when meetings is not null && Path.GetDirectoryName(problem.Subject) == meetings =>
                new(problem, "workspace.issues.unreachable", null, IssueAction.None, alarm: false),
            StoreProblemKind.Unreadable when problem.Subject == meetings =>
                new(problem, "workspace.issues.other", null, IssueAction.None, alarm: false),
            _ => new(problem, "workspace.issues.other", null, IssueAction.None),
        };
    }

    internal void OnLanguageChanged() => OnPropertyChanged(nameof(Cause));

    private static Localizer L => Localizer.Instance;
}
