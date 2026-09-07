using System.Text.Json;
using System.Text.Json.Serialization;
using Kanal.Core.Diagnostics;

namespace Kanal.Core.Workspaces;

public sealed class WorkspaceStore(string registryPath)
{
    public const int SchemaVersion = 1;
    public const string WorkspaceFileName = "kanal-workspace.json";
    public const string MeetingFileName = "meeting.json";
    private const string MeetingsFolderName = "meetings";
    private const string LogCategory = "workspaces";
    private const string NeedsTitle = "A meeting needs a title.";
    private const string NeedsName = "A workspace needs a name.";
    private const string NotAnId = "That is not a meeting id.";

    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    public WorkspaceListing ListWorkspaces()
    {
        var (entries, problem) = ReadRegistry();
        if (problem is not null)
            return new WorkspaceListing([], [problem]);

        var workspaces = new List<Workspace>();
        var problems = new List<StoreProblem>();
        foreach (var entry in entries)
        {
            if (!entry.Complete)
            {
                problems.Add(new StoreProblem(
                    StoreProblemKind.Unreadable, registryPath, "A row of the list is missing fields."));
                continue;
            }

            workspaces.Add(entry.ToWorkspace());
            if (!Directory.Exists(entry.RootPath))
                problems.Add(Unplugged(entry));
        }

        return new WorkspaceListing(workspaces, problems);
    }

    public WorkspaceResult CreateWorkspace(string name, string rootPath)
    {
        if (string.IsNullOrWhiteSpace(name))
            return Refused(rootPath, NeedsName);
        if (string.IsNullOrWhiteSpace(rootPath))
            return Refused(rootPath, "A workspace needs a folder.");
        if (File.Exists(rootPath))
            return Refused(rootPath, "That path is a file, not a folder.");

        rootPath = Absolute(rootPath);

        // Adopted under its own name, never overwritten: picking last year's folder means "open this".
        if (Directory.Exists(rootPath) && File.Exists(Path.Combine(rootPath, WorkspaceFileName)))
            return OpenWorkspace(rootPath);

        var (listed, trouble) = ReadRegistry();
        if (trouble is not null)
            return new WorkspaceResult(null, trouble);
        if (listed.FirstOrDefault(e => SamePath(e.RootPath!, rootPath)) is { } already)
            return Refused(
                rootPath, $"That folder is already the workspace \"{already.Name}\".");

        var workspace = new Workspace(NewId(), name.Trim(), rootPath, DateTimeOffset.UtcNow);
        try
        {
            Directory.CreateDirectory(Path.Combine(rootPath, MeetingsFolderName));
            WriteWorkspaceFile(workspace);
        }
        catch (Exception ex)
        {
            return new WorkspaceResult(null, Trouble(rootPath, "The workspace folder could not be written.", ex));
        }

        return Register(workspace);
    }

    public WorkspaceResult OpenWorkspace(string rootPath)
    {
        rootPath = Absolute(rootPath);
        if (!Directory.Exists(rootPath))
            return new WorkspaceResult(null, new StoreProblem(
                StoreProblemKind.FolderMissing, rootPath, "That folder is not there."));

        var path = Path.Combine(rootPath, WorkspaceFileName);
        if (!File.Exists(path))
            return Refused(rootPath, "That folder does not hold a workspace.");

        var (stored, problem) = Read<StoredWorkspace>(path);
        return problem is not null
            ? new WorkspaceResult(null, problem)
            : Register(stored!.ToWorkspace(rootPath));
    }

    public WorkspaceResult RenameWorkspace(string id, string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return Refused(registryPath, NeedsName);

        var (entries, problem) = ReadRegistry();
        if (problem is not null)
            return new WorkspaceResult(null, problem);

        var index = entries.FindIndex(e => e.Id == id);
        if (index < 0)
            return new WorkspaceResult(null, NoSuchWorkspace(id));

        var renamed = entries[index].ToWorkspace() with { Name = name.Trim() };
        entries[index] = StoredRegistryEntry.From(renamed);
        try
        {
            WriteRegistry(entries);
            if (Directory.Exists(renamed.RootPath))
                WriteWorkspaceFile(renamed);
        }
        catch (Exception ex)
        {
            return new WorkspaceResult(null, Trouble(renamed.RootPath, "The rename could not be written.", ex));
        }

        return new WorkspaceResult(renamed, null);
    }

    /// <summary>Leaves every file where it is: dropping a row from a list is not deleting a year of meetings.</summary>
    public StoreProblem? ForgetWorkspace(string id)
    {
        var (entries, problem) = ReadRegistry();
        if (problem is not null)
            return problem;
        if (entries.RemoveAll(e => e.Id == id) == 0)
            return NoSuchWorkspace(id);

        try
        {
            WriteRegistry(entries);
            return null;
        }
        catch (Exception ex)
        {
            return Trouble(registryPath, "The workspace list could not be written.", ex);
        }
    }

    public MeetingListing ListMeetings(string workspaceId)
    {
        var (workspace, problem) = Locate(workspaceId);
        if (problem is not null)
            return new MeetingListing([], [problem]);

        var folder = Path.Combine(workspace!.RootPath, MeetingsFolderName);
        if (!Directory.Exists(folder))
            return new MeetingListing([], []);

        List<string> folders;
        try
        {
            folders = [.. Directory.EnumerateDirectories(folder).Order()];
        }
        catch (Exception ex)
        {
            Log.Warning(LogCategory, $"{folder} could not be listed.", ex);
            return new MeetingListing([], [new StoreProblem(
                StoreProblemKind.Unreadable, folder, ex.Message)]);
        }

        var meetings = new List<MeetingRecord>();
        var problems = new List<StoreProblem>();
        foreach (var path in folders)
        {
            var file = Path.Combine(path, MeetingFileName);
            if (!File.Exists(file))
            {
                // An interrupted save: the operator watched it being created, so it cannot vanish.
                problems.Add(new StoreProblem(
                    StoreProblemKind.Unreadable, path, "That meeting folder holds no record."));
                continue;
            }

            var (stored, trouble) = Read<StoredMeeting>(file);
            if (trouble is not null)
                problems.Add(trouble);
            else if (Misfiled(stored!.Id, Path.GetFileName(path)))
                // A duplicated folder would otherwise list twice under one id, and only one of
                // the two could ever be renamed or deleted again.
                problems.Add(WrongFolder(path));
            else
                meetings.Add(stored.ToRecord(workspaceId));
        }

        return new MeetingListing(meetings, problems);
    }

    public MeetingResult CreateMeeting(string workspaceId, string title)
    {
        if (string.IsNullOrWhiteSpace(title))
            return RefusedMeeting(workspaceId, NeedsTitle);

        var (workspace, problem) = Locate(workspaceId);
        if (problem is not null)
            return new MeetingResult(null, problem);

        // The id decides where the bytes go, never the title: same title, same day is ordinary.
        var meeting = new MeetingRecord(
            NewId(), workspaceId, title.Trim(), DateTimeOffset.UtcNow, null, null, [], null, null);
        return SaveMeeting(meeting);
    }

    public MeetingResult SaveMeeting(MeetingRecord meeting)
    {
        if (!IsFolderName(meeting.Id))
            return RefusedMeeting(meeting.Id, NotAnId);
        if (string.IsNullOrWhiteSpace(meeting.Title))
            return RefusedMeeting(meeting.Id, NeedsTitle);
        if (!IsFileName(meeting.TranscriptFileName) || !IsFileName(meeting.AudioFileName))
            return RefusedMeeting(meeting.Id, "An artefact is named by file, not by path.");

        var (workspace, problem) = Locate(meeting.WorkspaceId);
        if (problem is not null)
            return new MeetingResult(null, problem);

        var folder = FolderFor(workspace!, meeting.Id);
        try
        {
            Directory.CreateDirectory(folder);
            File.WriteAllText(
                Path.Combine(folder, MeetingFileName),
                JsonSerializer.Serialize(StoredMeeting.From(meeting), Options));
        }
        catch (Exception ex)
        {
            return new MeetingResult(null, Trouble(folder, "The meeting could not be written.", ex));
        }

        return new MeetingResult(meeting, null);
    }

    public MeetingResult RenameMeeting(string workspaceId, string meetingId, string title)
    {
        if (string.IsNullOrWhiteSpace(title))
            return RefusedMeeting(meetingId, NeedsTitle);

        var (existing, problem) = ReadMeeting(workspaceId, meetingId);
        return problem is not null
            ? new MeetingResult(null, problem)
            : SaveMeeting(existing! with { Title = title.Trim() });
    }

    public StoreProblem? DeleteMeeting(string workspaceId, string meetingId)
    {
        var (folder, problem) = FolderOfMeeting(workspaceId, meetingId);
        if (problem is not null)
            return problem;
        if (!Directory.Exists(folder))
            return NoSuchMeeting(meetingId);

        try
        {
            Directory.Delete(folder!, recursive: true);
            return null;
        }
        catch (Exception ex)
        {
            return Trouble(folder!, "The meeting could not be deleted.", ex);
        }
    }

    public string? MeetingFolder(string workspaceId, string meetingId) =>
        FolderOfMeeting(workspaceId, meetingId).Folder;

    private (string? Folder, StoreProblem? Problem) FolderOfMeeting(string workspaceId, string meetingId)
    {
        // An id becomes a folder name, so ".." here would delete the workspace it lives in.
        if (!IsFolderName(meetingId))
            return (null, Invalid(meetingId, NotAnId));

        var (workspace, problem) = Locate(workspaceId);
        return problem is null ? (FolderFor(workspace!, meetingId), null) : (null, problem);
    }

    private (MeetingRecord? Meeting, StoreProblem? Problem) ReadMeeting(string workspaceId, string meetingId)
    {
        var (folder, problem) = FolderOfMeeting(workspaceId, meetingId);
        if (problem is not null)
            return (null, problem);

        var file = Path.Combine(folder!, MeetingFileName);
        if (!File.Exists(file))
            return (null, NoSuchMeeting(meetingId));

        var (stored, trouble) = Read<StoredMeeting>(file);
        if (trouble is not null)
            return (null, trouble);

        // A duplicated folder holds a record naming the original. Renaming through it would save
        // under that id and retitle the healthy meeting instead of the one that was asked for.
        return Misfiled(stored!.Id, meetingId)
            ? (null, WrongFolder(folder!))
            : (stored.ToRecord(workspaceId), null);
    }

    private static string FolderFor(Workspace workspace, string meetingId) =>
        Path.Combine(workspace.RootPath, MeetingsFolderName, meetingId);

    private (Workspace? Workspace, StoreProblem? Problem) Locate(string workspaceId)
    {
        var (entries, problem) = ReadRegistry();
        if (problem is not null)
            return (null, problem);

        var entry = entries.FirstOrDefault(e => e.Id == workspaceId && e.Complete);
        if (entry is null)
            return (null, NoSuchWorkspace(workspaceId));

        return Directory.Exists(entry.RootPath)
            ? (entry.ToWorkspace(), null)
            : (null, Unplugged(entry));
    }

    private (List<StoredRegistryEntry> Entries, StoreProblem? Problem) ReadRegistry()
    {
        if (!File.Exists(registryPath))
            return ([], null);

        var (registry, problem) = Read<StoredRegistry>(registryPath);
        return problem is not null ? ([], problem) : (registry!.Workspaces!, null);
    }

    private void WriteRegistry(List<StoredRegistryEntry> entries)
    {
        var folder = Path.GetDirectoryName(registryPath);
        if (!string.IsNullOrEmpty(folder))
            Directory.CreateDirectory(folder);

        File.WriteAllText(
            registryPath,
            JsonSerializer.Serialize(new StoredRegistry(SchemaVersion, entries), Options));
    }

    private static void WriteWorkspaceFile(Workspace workspace) =>
        File.WriteAllText(
            Path.Combine(workspace.RootPath, WorkspaceFileName),
            JsonSerializer.Serialize(StoredWorkspace.From(workspace), Options));

    private WorkspaceResult Register(Workspace workspace)
    {
        var (entries, problem) = ReadRegistry();
        if (problem is not null)
            return new WorkspaceResult(null, problem);

        var index = entries.FindIndex(e => e.Id == workspace.Id);
        if (index >= 0)
        {
            var listed = entries[index];

            // Two folders, one identity: a restored backup, a share mounted twice. Only a copy
            // while the folder already listed is still there — otherwise it is the same one, moved.
            if (!SamePath(listed.RootPath!, workspace.RootPath) && Directory.Exists(listed.RootPath))
                return Refused(
                    workspace.RootPath,
                    $"That folder is a copy of the workspace \"{listed.Name}\", " +
                    $"which is open at {listed.RootPath}.");

            // The list holds the name the operator last gave it. A rename made while the drive was
            // out could not reach the marker file, and must not be undone by reading it back.
            workspace = workspace with { Name = listed.Name! };
            entries[index] = StoredRegistryEntry.From(workspace);
        }
        else
        {
            entries.Add(StoredRegistryEntry.From(workspace));
        }

        try
        {
            WriteRegistry(entries);
        }
        catch (Exception ex)
        {
            return new WorkspaceResult(null, Trouble(registryPath, "The workspace list could not be written.", ex));
        }

        return new WorkspaceResult(workspace, null);
    }

    private static (T? Value, StoreProblem? Problem) Read<T>(string path) where T : class, IStoredRecord
    {
        T? value;
        try
        {
            value = JsonSerializer.Deserialize<T>(File.ReadAllText(path), Options);
        }
        catch (Exception ex)
        {
            Log.Warning(LogCategory, $"{path} could not be read.", ex);
            return (null, new StoreProblem(StoreProblemKind.Unreadable, path, ex.Message));
        }

        if (value is null)
            return (null, new StoreProblem(StoreProblemKind.Unreadable, path, "The file is empty."));

        // Refused, not half-read: the next save would write the dropped fields back as loss.
        if (value.SchemaVersion > SchemaVersion)
            return (null, new StoreProblem(
                StoreProblemKind.UnsupportedVersion, path,
                $"Written by a newer version of Kanal (schema {value.SchemaVersion})."));

        // Well-formed JSON is not a well-formed record: a missing field deserializes to null.
        return value.Complete
            ? (value, null)
            : (null, new StoreProblem(StoreProblemKind.Unreadable, path, "The record is missing fields."));
    }

    private static string NewId() => Guid.NewGuid().ToString("N")[..16];

    private static bool IsFolderName(string? id) =>
        !string.IsNullOrEmpty(id) && id.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');

    // A name, never a path: these are resolved against the meeting's own folder.
    private static bool IsFileName(string? name) =>
        name is null
        || (!string.IsNullOrWhiteSpace(name)
            && !name.Contains('/') && !name.Contains('\\')
            && name is not ("." or ".."));

    private static bool Misfiled(string? recordId, string folderName) =>
        !string.Equals(recordId, folderName, OperatingSystem.IsLinux()
            ? StringComparison.Ordinal
            : StringComparison.OrdinalIgnoreCase);

    private static StoreProblem WrongFolder(string path) =>
        new(StoreProblemKind.Unreadable, path,
            "That meeting record does not belong to the folder it is in.");

    private static bool SamePath(string a, string b) =>
        string.Equals(a, b, OperatingSystem.IsLinux()
            ? StringComparison.Ordinal
            : StringComparison.OrdinalIgnoreCase);

    // Two spellings of one folder must not become two workspaces, so every link on the way down
    // is resolved: on macOS /tmp and /var are themselves symlinks, which makes this the usual case.
    private static string Absolute(string path)
    {
        try
        {
            var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
            for (var hops = 0; hops < 40; hops++)
            {
                var (resolved, followed) = FollowFirstLink(full);
                if (!followed)
                    return resolved;

                full = resolved;
            }

            return full; // a loop of links; the folder checks will say what is wrong with it
        }
        catch (Exception)
        {
            return path; // let the folder checks phrase it; this is not the place to fail
        }
    }

    // Resolving one link can expose another above it, so this returns after the first and the
    // caller walks again from the top.
    private static (string Path, bool Followed) FollowFirstLink(string full)
    {
        var walked = Path.GetPathRoot(full) ?? string.Empty;
        var parts = full[walked.Length..]
            .Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);

        for (var i = 0; i < parts.Length; i++)
        {
            walked = Path.Combine(walked, parts[i]);
            if (new DirectoryInfo(walked).ResolveLinkTarget(returnFinalTarget: true) is not { } target)
                continue;

            var rest = Path.Combine([.. parts[(i + 1)..]]);
            return (Path.TrimEndingDirectorySeparator(
                Path.GetFullPath(Path.Combine(target.FullName, rest))), true);
        }

        return (full, false);
    }

    private static StoreProblem NoSuchWorkspace(string id) =>
        new(StoreProblemKind.NotFound, id, "No such workspace.");

    private static StoreProblem NoSuchMeeting(string id) =>
        new(StoreProblemKind.NotFound, id, "No such meeting.");

    private static StoreProblem Unplugged(StoredRegistryEntry entry) =>
        new(StoreProblemKind.FolderMissing, entry.RootPath!,
            $"The folder for workspace \"{entry.Name}\" is not there.");

    private static StoreProblem Invalid(string subject, string detail) =>
        new(StoreProblemKind.Invalid, subject, detail);

    private static WorkspaceResult Refused(string subject, string detail) =>
        new(null, Invalid(subject, detail));

    private static MeetingResult RefusedMeeting(string subject, string detail) =>
        new(null, Invalid(subject, detail));

    private static StoreProblem Trouble(string path, string detail, Exception cause)
    {
        Log.Warning(LogCategory, $"{detail} ({path})", cause);
        return new StoreProblem(StoreProblemKind.Unwritable, path, $"{detail} {cause.Message}");
    }

    private interface IStoredRecord
    {
        int SchemaVersion { get; }

        [JsonIgnore]
        bool Complete { get; }
    }

    private sealed record StoredRegistry(int SchemaVersion, List<StoredRegistryEntry>? Workspaces)
        : IStoredRecord
    {
        [JsonIgnore]
        public bool Complete => Workspaces is not null;
    }

    private sealed record StoredRegistryEntry(
        string? Id, string? Name, string? RootPath, DateTimeOffset CreatedAt)
    {
        internal bool Complete =>
            !string.IsNullOrWhiteSpace(Id)
            && !string.IsNullOrWhiteSpace(Name)
            && !string.IsNullOrWhiteSpace(RootPath);

        internal static StoredRegistryEntry From(Workspace w) =>
            new(w.Id, w.Name, w.RootPath, w.CreatedAt);

        internal Workspace ToWorkspace() => new(Id!, Name!, RootPath!, CreatedAt);
    }

    // No path field: the folder it is in is its path, and a stale copy would outrank the truth.
    private sealed record StoredWorkspace(
        int SchemaVersion, string? Id, string? Name, DateTimeOffset CreatedAt) : IStoredRecord
    {
        [JsonIgnore]
        public bool Complete => !string.IsNullOrWhiteSpace(Id) && !string.IsNullOrWhiteSpace(Name);

        internal static StoredWorkspace From(Workspace w) =>
            new(WorkspaceStore.SchemaVersion, w.Id, w.Name, w.CreatedAt);

        internal Workspace ToWorkspace(string rootPath) => new(Id!, Name!, rootPath, CreatedAt);
    }

    private sealed record StoredMeeting(
        int SchemaVersion,
        string? Id,
        string? Title,
        DateTimeOffset CreatedAt,
        DateTimeOffset? StartedAt,
        DateTimeOffset? EndedAt,
        List<string>? Languages,
        string? TranscriptFileName,
        string? AudioFileName) : IStoredRecord
    {
        [JsonIgnore]
        public bool Complete => !string.IsNullOrWhiteSpace(Id) && !string.IsNullOrWhiteSpace(Title);

        internal static StoredMeeting From(MeetingRecord m) =>
            new(WorkspaceStore.SchemaVersion, m.Id, m.Title, m.CreatedAt, m.StartedAt, m.EndedAt,
                [.. m.Languages], m.TranscriptFileName, m.AudioFileName);

        internal MeetingRecord ToRecord(string workspaceId) =>
            new(Id!, workspaceId, Title!, CreatedAt, StartedAt, EndedAt,
                Languages ?? [], TranscriptFileName, AudioFileName);
    }
}
