using Kanal.Core.Diagnostics;
using Kanal.Core.Models;

namespace Kanal.Core.Workspaces;

internal sealed class MeetingRecords(WorkspaceRegistry registry)
{
    public const string FileName = "meeting.json";
    public const string MeetingsFolderName = "meetings";
    private const string SetAsideFolderName = "kanal-set-aside";
    private const string UnknownLanguage = "und";
    private const string NeedsTitle = "A meeting needs a title.";

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
            return new MeetingListing([], [StoreProblems.Unreadable(folder, ex.Message)]);
        }

        var meetings = new List<MeetingRecord>();
        var problems = new List<StoreProblem>();
        foreach (var path in folders)
        {
            var (stored, trouble) = ReadFolder(path);
            if (trouble is not null)
                problems.Add(trouble);
            else
                meetings.Add(stored!.ToRecord(workspaceId, path));
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
            StorePaths.NewId(), workspaceId, title.Trim(), DateTimeOffset.UtcNow, null, null, [], []);
        return Save(meeting);
    }

    public MeetingResult Save(MeetingRecord meeting)
    {
        if (!StorePaths.IsFolderName(meeting.Id))
            return new MeetingResult(null, StoreProblems.NotAMeetingId(meeting.Id));
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
        if (meeting.Runs.Any(r => !StorePaths.IsArtifactPath(r.TranscriptPath, folder)
                                  || !StorePaths.IsArtifactPath(r.AudioPath, folder)))
            return Refused(meeting.Id, "An artefact path must name a file inside its meeting folder.");

        try
        {
            Directory.CreateDirectory(folder);
            RecordFile.Write(Path.Combine(folder, FileName), StoredMeeting.From(meeting, folder));
        }
        catch (Exception ex)
        {
            const string failure = "The meeting could not be written.";
            Log.Warning(StoreProblems.LogCategory, $"{failure} ({folder})", ex);
            return new MeetingResult(null, StoreProblems.Unwritable(folder, failure, ex));
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
            const string failure = "The meeting could not be deleted.";
            Log.Warning(StoreProblems.LogCategory, $"{failure} ({folder})", ex);
            return StoreProblems.Unwritable(folder!, failure, ex);
        }
    }

    public RecoverResult Recover(string workspaceId, string folder, string baseTitle)
    {
        if (string.IsNullOrWhiteSpace(baseTitle))
            return new RecoverResult(null, null, StoreProblems.Invalid(folder, NeedsTitle));

        var (path, problem) = MeetingsSubfolder(workspaceId, folder);
        if (problem is not null)
            return new RecoverResult(null, null, problem);

        var (_, state) = ReadFolder(path!);
        if (state is null)
            return new RecoverResult(null, null, StoreProblems.Invalid(path!, "That meeting record is not damaged."));
        if (state.Kind is not (StoreProblemKind.RecordMissing or StoreProblemKind.RecordDamaged))
            return new RecoverResult(null, null, state);

        var runs = StorePaths.IsFolderName(Path.GetFileName(path!)) ? RunsOnDisk(path!) : [];
        var utterances = runs.SelectMany(r => TranscriptLog.Read(r.TranscriptPath)).ToList();
        var languages = LanguagesOf(utterances);
        if (languages.Count == 0)
            return SetAside(workspaceId, path!);

        var record = Rebuilt(
            workspaceId, path!, runs, utterances, languages, FreeTitle(workspaceId, baseTitle, exceptMeetingId: null));
        var recordFile = Path.Combine(path!, FileName);
        string? damaged = null;
        try
        {
            if (File.Exists(recordFile) || Directory.Exists(recordFile))
                damaged = MoveAsideDamagedRecord(recordFile);
        }
        catch (Exception ex)
        {
            const string failure = "The damaged record could not be renamed.";
            Log.Warning(StoreProblems.LogCategory, $"{failure} ({path})", ex);
            return new RecoverResult(null, null, StoreProblems.Unwritable(path!, failure, ex));
        }

        var saved = Save(record);
        if (saved.Problem is not null && damaged is not null)
            RestoreDamagedRecord(damaged, recordFile);

        return new RecoverResult(saved.Meeting, null, saved.Problem);
    }

    public RecoverResult Reidentify(string workspaceId, string folder)
    {
        var (path, problem) = MeetingsSubfolder(workspaceId, folder);
        if (problem is not null)
            return new RecoverResult(null, null, problem);

        var (stored, state) = ReadFolder(path!);
        if (state is null)
            return new RecoverResult(
                null, null, StoreProblems.Invalid(path!, "That meeting record already belongs to its folder."));
        if (state.Kind != StoreProblemKind.Misfiled)
            return new RecoverResult(null, null, state);

        var name = Path.GetFileName(path!);
        if (!StorePaths.IsFolderName(name))
            return SetAside(workspaceId, path!);

        var title = FreeTitle(workspaceId, stored!.Title!, exceptMeetingId: name);
        var saved = Save(stored.ToRecord(workspaceId, path!) with { Id = name, Title = title });
        return new RecoverResult(saved.Meeting, null, saved.Problem);
    }

    public (string? Folder, StoreProblem? Problem) FolderOf(string workspaceId, string meetingId)
    {
        // An id becomes a folder name, so ".." here would delete the workspace it lives in.
        if (!StorePaths.IsFolderName(meetingId))
            return (null, StoreProblems.NotAMeetingId(meetingId));

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

        var (stored, trouble) = ReadFolder(folder!);
        return trouble is not null ? (null, trouble) : (stored!.ToRecord(workspaceId, folder!), null);
    }

    private static (StoredMeeting? Stored, StoreProblem? Problem) ReadFolder(string folder)
    {
        var file = Path.Combine(folder, FileName);
        if (!File.Exists(file) && !Directory.Exists(file))
            // An interrupted save: the operator watched it being created, so it cannot vanish.
            return (null, new StoreProblem(
                StoreProblemKind.RecordMissing, folder, "That meeting folder holds no record."));

        // Only content is damage. A read that fails on access or I/O says nothing about the bytes,
        // and recovery would rename a healthy record.
        if (Directory.Exists(file))
            return (null, new StoreProblem(
                StoreProblemKind.RecordDamaged, folder, "The meeting record is a folder, not a file."));

        var (text, unreachable) = RecordFile.ReadText(file);
        if (unreachable is not null)
            return (null, unreachable with { Subject = folder });

        var (stored, trouble) = RecordFile.Parse<StoredMeeting>(text!, file, RecordFile.MeetingSchemaVersion);
        if (trouble is not null)
            return (null, trouble.Kind == StoreProblemKind.Unreadable
                ? trouble with { Kind = StoreProblemKind.RecordDamaged, Subject = folder }
                : trouble);

        // A duplicated folder holds a record naming the original. Listing or renaming through it
        // would address the healthy meeting instead of the one that was asked for.
        return Misfiled(stored!.Id, Path.GetFileName(folder))
            ? (stored, StoreProblems.WrongFolder(folder))
            : (stored, null);
    }

    private (string? Folder, StoreProblem? Problem) MeetingsSubfolder(string workspaceId, string folder)
    {
        var (workspace, problem) = registry.Locate(workspaceId);
        if (problem is not null)
            return (null, problem);

        var meetings = Path.GetFullPath(FolderIn(workspace!.RootPath));
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
        if (!StorePaths.SamePath(Path.GetDirectoryName(full) ?? "", meetings))
            return (null, StoreProblems.Invalid(folder, "That folder is not a meeting folder of this workspace."));

        return Directory.Exists(full)
            ? (full, null)
            : (null, new StoreProblem(StoreProblemKind.NotFound, full, "That meeting folder is not there."));
    }

    private static List<string> LanguagesOf(IReadOnlyList<Utterance> utterances) =>
        [.. utterances
            .SelectMany(u => u.Translations.Keys.Prepend(u.SrcLang))
            .Where(IsKnownLanguage)
            .Distinct(StringComparer.OrdinalIgnoreCase)];

    private static bool IsKnownLanguage(string language) =>
        !string.IsNullOrWhiteSpace(language)
        && !string.Equals(language, UnknownLanguage, StringComparison.OrdinalIgnoreCase);

    // Every n up to the file count: a run number skips a name that is already taken.
    private static List<MeetingRun> RunsOnDisk(string folder) =>
        [.. Enumerable.Range(1, Directory.EnumerateFiles(folder).Count())
            .Select(n => new MeetingRun(
                Path.Combine(folder, MeetingRun.TranscriptFileName(n)),
                Path.Combine(folder, MeetingRun.AudioFileName(n)), null, null))
            .Where(r => File.Exists(r.TranscriptPath))
            .Select(r => File.Exists(r.AudioPath) ? r : r with { AudioPath = null })];

    private static MeetingRecord Rebuilt(
        string workspaceId, string folder, List<MeetingRun> runs, IReadOnlyList<Utterance> utterances,
        List<string> languages, string baseTitle)
    {
        var ended = runs.SelectMany(r => new[] { r.TranscriptPath, r.AudioPath })
            .OfType<string>()
            .Select(File.GetLastWriteTimeUtc)
            .Max();
        var spanMs = Math.Max(0, utterances.Max(u => u.TEndMs ?? u.TStartMs) - utterances.Min(u => u.TStartMs));
        var started = new DateTimeOffset(ended).AddMilliseconds(-spanMs);

        return new MeetingRecord(
            Path.GetFileName(folder), workspaceId, baseTitle.Trim(), started, started,
            new DateTimeOffset(ended), languages, runs);
    }

    private static string MoveAsideDamagedRecord(string record)
    {
        var target = record + ".damaged";
        for (var n = 2; File.Exists(target) || Directory.Exists(target); n++)
            target = $"{record}.damaged.{n}";

        if (Directory.Exists(record))
            Directory.Move(record, target);
        else
            File.Move(record, target);
        return target;
    }

    private static void RestoreDamagedRecord(string damaged, string record)
    {
        try
        {
            if (Directory.Exists(damaged))
                Directory.Move(damaged, record);
            else
                File.Move(damaged, record, overwrite: true);
        }
        catch (Exception ex)
        {
            Log.Warning(StoreProblems.LogCategory, $"{damaged} could not be renamed back.", ex);
        }
    }

    private RecoverResult SetAside(string workspaceId, string folder)
    {
        var (workspace, problem) = registry.Locate(workspaceId);
        if (problem is not null)
            return new RecoverResult(null, null, problem);

        try
        {
            var root = Path.Combine(workspace!.RootPath, SetAsideFolderName);
            Directory.CreateDirectory(root);
            var target = Path.Combine(root, Path.GetFileName(folder));
            for (var n = 2; Directory.Exists(target) || File.Exists(target); n++)
                target = Path.Combine(root, $"{Path.GetFileName(folder)}-{n}");

            Directory.Move(folder, target);
            return new RecoverResult(null, target, null);
        }
        catch (Exception ex)
        {
            const string failure = "The meeting folder could not be moved.";
            Log.Warning(StoreProblems.LogCategory, $"{failure} ({folder})", ex);
            return new RecoverResult(null, null, StoreProblems.Unwritable(folder, failure, ex));
        }
    }

    private static string FolderFor(Workspace workspace, string meetingId) =>
        Path.Combine(FolderIn(workspace.RootPath), meetingId);

    private static bool Misfiled(string? recordId, string folderName) =>
        !string.Equals(recordId, folderName, StorePaths.NameComparison);

    private static MeetingResult Refused(string subject, string detail) =>
        new(null, StoreProblems.Invalid(subject, detail));
}
