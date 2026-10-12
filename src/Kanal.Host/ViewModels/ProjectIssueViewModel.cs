using System;
using System.IO;
using Kanal.Core.Workspaces;
using Kanal.Host.Localization;

namespace Kanal.Host.ViewModels;

public enum IssueOrigin
{
    Project,

    ProjectListRow,

    ProjectList,
}

public sealed class ProjectIssueViewModel : ViewModelBase
{
    private readonly string _causeKey;
    private readonly string? _projectName;

    private ProjectIssueViewModel(StoreProblem problem, string causeKey, string? projectName)
    {
        Problem = problem;
        _causeKey = causeKey;
        _projectName = projectName;
    }

    public StoreProblem Problem { get; }

    public StoreProblemKind Kind => Problem.Kind;

    public string Subject => Problem.Subject;

    public string Location =>
        Path.GetFileName(Subject) == WorkspaceStore.MeetingFileName ? Path.GetDirectoryName(Subject)! : Subject;

    public bool IsAlarm => Kind != StoreProblemKind.FolderMissing;

    public string Cause => _projectName is null ? L[_causeKey] : L.Format(_causeKey, _projectName);

    public static ProjectIssueViewModel For(StoreProblem problem, Workspace? owner, IssueOrigin origin = IssueOrigin.Project)
    {
        if (origin == IssueOrigin.ProjectList)
            return new(problem, "workspace.issues.projectlistunreadable", null);
        if (origin == IssueOrigin.ProjectListRow)
            return new(problem, "workspace.issues.projectlist", null);

        var meetings = owner is null ? null : Path.Combine(owner.RootPath, WorkspaceStore.MeetingsFolderName);
        return problem.Kind switch
        {
            StoreProblemKind.FolderMissing when owner is not null && problem.Subject == owner.RootPath =>
                new(problem, "workspace.issues.projectmissing", owner.Name),
            StoreProblemKind.FolderMissing when problem.Subject == meetings =>
                new(problem, "workspace.issues.meetingsmissing", null),
            StoreProblemKind.FolderMissing => new(problem, "workspace.issues.foldermissing", null),
            StoreProblemKind.UnsupportedVersion => new(problem, "workspace.issues.format", null),
            StoreProblemKind.Unreadable when meetings is not null && problem.Subject.StartsWith(
                meetings + Path.DirectorySeparatorChar, StringComparison.Ordinal) =>
                new(problem, "workspace.issues.unreadable", null),
            _ => new(problem, "workspace.issues.other", null),
        };
    }

    internal void OnLanguageChanged() => OnPropertyChanged(nameof(Cause));

    private static Localizer L => Localizer.Instance;
}
