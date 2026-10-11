using Kanal.Core.Diagnostics;

namespace Kanal.Core.Workspaces;

internal sealed class MeetingRecords(WorkspaceRegistry registry)
{
    public const string FileName = "meeting.json";
    private const string MeetingsFolderName = "meetings";
    private const string NeedsTitle = "A meeting needs a title.";
    private const string NotAnId = "That is not a meeting id.";

    public static string FolderIn(string workspaceRoot) => Path.Combine(workspaceRoot, MeetingsFolderName);

    public MeetingListing List(string workspaceId)
    {
        var (workspace, problem) = registry.Locate(workspaceId);
        if (problem is not null)
            return new MeetingListing([], [problem]);

        var folder = FolderIn(workspace!.RootPath);
        if (!Directory.Exists(folder))
            return new MeetingListing([], [new StoreProblem(
                StoreProblemKind.FolderMissing, folder, "The meetings folder is not there.")]);

        List<string> folders;
        try
        {
            folders = [.. Directory.EnumerateDirectories(folder).Order()];
        }
        catch (Exception ex)
        {
            Log.Warning(StoreProblems.LogCategory, $"{folder} could not be listed.", ex);
            return new MeetingListing([], [new StoreProblem(
                StoreProblemKind.Unreadable, folder, ex.Message)]);
        }

        var meetings = new List<MeetingRecord>();
        var problems = new List<StoreProblem>();
        foreach (var path in folders)
        {
            var file = Path.Combine(path, FileName);
            if (!File.Exists(file) && !Directory.Exists(file))
            {
                // An interrupted save: the operator watched it being created, so it cannot vanish.
                problems.Add(new StoreProblem(
                    StoreProblemKind.Unreadable, path, "That meeting folder holds no record."));
                continue;
            }

            var (stored, trouble) = RecordFile.Read<StoredMeeting>(file);
            if (trouble is not null)
                problems.Add(trouble);
            else if (Misfiled(stored!.Id, Path.GetFileName(path)))
                // A duplicated folder would otherwise list twice under one id, and only one of
                // the two could ever be renamed or deleted again.
                problems.Add(StoreProblems.WrongFolder(path));
            else
                meetings.Add(stored.ToRecord(workspaceId, path));
        }

        return new MeetingListing(meetings, problems);
    }

    public MeetingResult Create(string workspaceId, string title)
    {
        if (string.IsNullOrWhiteSpace(title))
            return Refused(workspaceId, NeedsTitle);

        var (_, problem) = registry.Locate(workspaceId);
        if (problem is not null)
            return new MeetingResult(null, problem);

        // The id decides where the bytes go, never the title. Creation stays exempt from the
        // uniqueness rule the rename paths carry (ADR 0054, decision 11): every record starts as
        // the same placeholder, and numbering that would survive into the default title.
        var meeting = new MeetingRecord(
            StorePaths.NewId(), workspaceId, title.Trim(), DateTimeOffset.UtcNow, null, null, [], null, null);
        return Save(meeting);
    }

    public MeetingResult Save(MeetingRecord meeting)
    {
        if (!StorePaths.IsFolderName(meeting.Id))
            return Refused(meeting.Id, NotAnId);
        if (string.IsNullOrWhiteSpace(meeting.Title))
            return Refused(meeting.Id, NeedsTitle);
        if (meeting.CreatedAt == default)
            return Refused(meeting.Id, "A meeting needs a creation time.");
        if (meeting.Languages is null || meeting.Languages.Any(string.IsNullOrWhiteSpace))
            return Refused(meeting.Id, "Meeting languages cannot be missing or blank.");
        var (workspace, problem) = registry.Locate(meeting.WorkspaceId);
        if (problem is not null)
            return new MeetingResult(null, problem);

        var folder = FolderFor(workspace!, meeting.Id);
        if (!StorePaths.IsArtifactPath(meeting.TranscriptPath, folder)
            || !StorePaths.IsArtifactPath(meeting.AudioPath, folder))
            return Refused(meeting.Id, "An artefact path must name a file inside its meeting folder.");

        try
        {
            Directory.CreateDirectory(folder);
            RecordFile.Write(Path.Combine(folder, FileName), StoredMeeting.From(meeting, folder));
        }
        catch (Exception ex)
        {
            return new MeetingResult(null, StoreProblems.Unwritable(folder, "The meeting could not be written.", ex));
        }

        return new MeetingResult(meeting, null);
    }

    public MeetingResult Rename(string workspaceId, string meetingId, string title)
    {
        if (string.IsNullOrWhiteSpace(title))
            return Refused(meetingId, NeedsTitle);

        var (existing, problem) = Read(workspaceId, meetingId);
        if (problem is not null)
            return new MeetingResult(null, problem);

        var wanted = title.Trim();
        return TitlesBesides(workspaceId, meetingId).Contains(wanted)
            ? new MeetingResult(null, new StoreProblem(
                StoreProblemKind.TitleTaken, wanted,
                $"Another meeting in this workspace is already called \"{wanted}\"."))
            : Save(existing! with { Title = wanted });
    }

    public string FreeTitle(string workspaceId, string title, string? exceptMeetingId)
    {
        var taken = TitlesBesides(workspaceId, exceptMeetingId);
        var wanted = title.Trim();
        var candidate = wanted;
        for (var n = 2; taken.Contains(candidate); n++)
            candidate = $"{wanted} {n}";

        return candidate;
    }

    public StoreProblem? Delete(string workspaceId, string meetingId)
    {
        var (folder, problem) = FolderOf(workspaceId, meetingId);
        if (problem is not null)
            return problem;
        if (!Directory.Exists(folder))
            return StoreProblems.NoSuchMeeting(meetingId);

        try
        {
            Directory.Delete(folder!, recursive: true);
            return null;
        }
        catch (Exception ex)
        {
            return StoreProblems.Unwritable(folder!, "The meeting could not be deleted.", ex);
        }
    }

    public (string? Folder, StoreProblem? Problem) FolderOf(string workspaceId, string meetingId)
    {
        // An id becomes a folder name, so ".." here would delete the workspace it lives in.
        if (!StorePaths.IsFolderName(meetingId))
            return (null, StoreProblems.Invalid(meetingId, NotAnId));

        var (workspace, problem) = registry.Locate(workspaceId);
        return problem is null ? (FolderFor(workspace!, meetingId), null) : (null, problem);
    }

    // Case- and accent-insensitive: "Tooling review" and "tooling review" are one title on the
    // sidebar, and the refusal has to read the same way the list does.
    private HashSet<string> TitlesBesides(string workspaceId, string? meetingId) =>
        new(
            List(workspaceId).Meetings
                .Where(m => m.Id != meetingId)
                .Select(m => m.Title),
            StringComparer.CurrentCultureIgnoreCase);

    private (MeetingRecord? Meeting, StoreProblem? Problem) Read(string workspaceId, string meetingId)
    {
        var (folder, problem) = FolderOf(workspaceId, meetingId);
        if (problem is not null)
            return (null, problem);

        var file = Path.Combine(folder!, FileName);
        if (!File.Exists(file))
            return (null, StoreProblems.NoSuchMeeting(meetingId));

        var (stored, trouble) = RecordFile.Read<StoredMeeting>(file);
        if (trouble is not null)
            return (null, trouble);

        // A duplicated folder holds a record naming the original. Renaming through it would save
        // under that id and retitle the healthy meeting instead of the one that was asked for.
        return Misfiled(stored!.Id, meetingId)
            ? (null, StoreProblems.WrongFolder(folder!))
            : (stored.ToRecord(workspaceId, folder!), null);
    }

    private static string FolderFor(Workspace workspace, string meetingId) =>
        Path.Combine(FolderIn(workspace.RootPath), meetingId);

    private static bool Misfiled(string? recordId, string folderName) =>
        !string.Equals(recordId, folderName, StorePaths.NameComparison);

    private static MeetingResult Refused(string subject, string detail) =>
        new(null, StoreProblems.Invalid(subject, detail));
}
