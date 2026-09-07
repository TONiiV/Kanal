namespace Kanal.Core.Workspaces;

/// <summary>A folder holding several meetings. Project, company, team: peers, never nested (ADR 0051).</summary>
public sealed record Workspace(
    string Id,
    string Name,
    string RootPath,
    DateTimeOffset CreatedAt);

/// <summary>File names are relative to the meeting's folder: a workspace survives being moved or re-mounted.</summary>
public sealed record MeetingRecord(
    string Id,
    string WorkspaceId,
    string Title,
    DateTimeOffset CreatedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? EndedAt,
    IReadOnlyList<string> Languages,
    string? TranscriptFileName,
    string? AudioFileName);

public enum StoreProblemKind
{
    NotFound,

    /// <summary>Known to the list, but the folder is not there — an unplugged drive, a moved directory.</summary>
    FolderMissing,

    Unreadable,

    UnsupportedVersion,
}

/// <summary>Always reported alongside what could be read: one bad record must not empty the list.</summary>
public sealed record StoreProblem(StoreProblemKind Kind, string Path, string Detail);

public sealed record WorkspaceListing(
    IReadOnlyList<Workspace> Workspaces,
    IReadOnlyList<StoreProblem> Problems);

public sealed record MeetingListing(
    IReadOnlyList<MeetingRecord> Meetings,
    IReadOnlyList<StoreProblem> Problems);

public sealed record WorkspaceResult(Workspace? Workspace, StoreProblem? Problem);

public sealed record MeetingResult(MeetingRecord? Meeting, StoreProblem? Problem);
