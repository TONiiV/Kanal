namespace Kanal.Core.Workspaces;

// Peers, never nested: there is no company-above-project level here (ADR 0051).
public sealed record Workspace(
    string Id,
    string Name,
    string RootPath,
    DateTimeOffset CreatedAt);

public sealed record MeetingRecord(
    string Id,
    string WorkspaceId,
    string Title,
    DateTimeOffset CreatedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? EndedAt,
    IReadOnlyList<string> Languages,
    string? TranscriptPath,
    string? AudioPath);

public enum StoreProblemKind
{
    Invalid,

    NotFound,

    FolderMissing,

    Unreadable,

    Unwritable,

    UnsupportedVersion,
}

// Subject, not Path: a NotFound names the id that was asked for, not somewhere on disk.
public sealed record StoreProblem(StoreProblemKind Kind, string Subject, string Detail);

public sealed record WorkspaceListing(
    IReadOnlyList<Workspace> Workspaces,
    IReadOnlyList<StoreProblem> Problems);

public sealed record MeetingListing(
    IReadOnlyList<MeetingRecord> Meetings,
    IReadOnlyList<StoreProblem> Problems);

public sealed record WorkspaceResult(Workspace? Workspace, StoreProblem? Problem);

public sealed record MeetingResult(MeetingRecord? Meeting, StoreProblem? Problem);
