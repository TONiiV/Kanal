namespace Kanal.Core.Workspaces;

// Peers, never nested: there is no company-above-project level here (ADR 0051).
// IconFile is a file name inside RootPath: the icon travels with the folder.
public sealed record Workspace(
    string Id,
    string Name,
    string RootPath,
    DateTimeOffset CreatedAt,
    string? IconGlyph = null,
    string? IconFile = null)
{
    public string? IconPath => IconFile is null ? null : Path.Combine(RootPath, IconFile);
}

public sealed record MeetingRecord(
    string Id,
    string WorkspaceId,
    string Title,
    DateTimeOffset CreatedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? EndedAt,
    IReadOnlyList<string> Languages,
    IReadOnlyList<MeetingRun> Runs)
{
    public MeetingRecord WithLastRun(Func<MeetingRun, MeetingRun> change) =>
        Runs.Count == 0 ? this : this with { Runs = [.. Runs.SkipLast(1), change(Runs[^1])] };

    // The transcript log truncates the file it opens, and an imported record may hold any plain file name.
    public int NextRun(string folder)
    {
        var held = Runs.SelectMany(r => new[] { r.TranscriptPath, r.AudioPath })
            .OfType<string>()
            .Select(p => Path.GetFileName(p))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        bool Taken(string name) => held.Contains(name) || File.Exists(Path.Combine(folder, name));

        var run = Runs.Count + 1;
        while (Taken(MeetingRun.TranscriptFileName(run)) || Taken(MeetingRun.AudioFileName(run)))
            run++;
        return run;
    }
}

// One Start…Stop run. Each run keeps its own files because the ASR restarts its utterance ids at zero.
public sealed record MeetingRun(
    string TranscriptPath,
    string? AudioPath,
    DateTimeOffset? StartedAt,
    DateTimeOffset? EndedAt)
{
    public static string TranscriptFileName(int run) =>
        run == 1 ? TranscriptLog.FileName : $"transcript-{run}.jsonl";

    public static string AudioFileName(int run) =>
        run == 1 ? WorkspaceStore.AudioFileName : $"audio-{run}.wav";
}

public enum StoreProblemKind
{
    Invalid,

    NotFound,

    FolderMissing,

    Unreadable,

    TitleTaken,

    Unwritable,

    UnsupportedVersion,

    TooLarge,

    WrongType,

    NotThisWorkspace,

    RecordMissing,

    RecordDamaged,

    Misfiled,

    RegistryRowDamaged,
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

public sealed record RecoverResult(MeetingRecord? Recovered, string? SetAsidePath, StoreProblem? Problem);
