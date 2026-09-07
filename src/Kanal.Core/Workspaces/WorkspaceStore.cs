using System.Text.Json;
using System.Text.Json.Serialization;
using Kanal.Core.Diagnostics;

namespace Kanal.Core.Workspaces;

// The registry of which folders are workspaces lives outside them all: a workspace on a drive
// that is not plugged in still has to appear in the list.
public sealed class WorkspaceStore(string registryPath)
{
    public const int SchemaVersion = 1;
    public const string WorkspaceFileName = "kanal-workspace.json";
    public const string MeetingFileName = "meeting.json";
    private const string MeetingsFolderName = "meetings";
    private const string LogCategory = "workspaces";

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
            workspaces.Add(entry.ToWorkspace());
            if (!Directory.Exists(entry.RootPath))
                problems.Add(Unplugged(entry));
        }

        return new WorkspaceListing(workspaces, problems);
    }

    public WorkspaceResult CreateWorkspace(string name, string rootPath)
    {
        if (string.IsNullOrWhiteSpace(name))
            return Refused(rootPath, "A workspace needs a name.");
        if (string.IsNullOrWhiteSpace(rootPath))
            return Refused(rootPath, "A workspace needs a folder.");
        if (File.Exists(rootPath))
            return Refused(rootPath, "That path is a file, not a folder.");

        // Adopted under its own name, never overwritten: picking last year's folder means "open this".
        if (Directory.Exists(rootPath) && File.Exists(Path.Combine(rootPath, WorkspaceFileName)))
            return OpenWorkspace(rootPath);

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
            return Refused(_registryPath, "A workspace needs a name.");

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
            return Trouble(_registryPath, "The workspace list could not be written.", ex);
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

        var meetings = new List<MeetingRecord>();
        var problems = new List<StoreProblem>();
        foreach (var path in Directory.EnumerateDirectories(folder).Order())
        {
            var file = Path.Combine(path, MeetingFileName);
            if (!File.Exists(file))
            {
                // An interrupted save leaves the folder without its record. Say so: a meeting the
                // operator watched being created must not just be absent from the list.
                problems.Add(new StoreProblem(
                    StoreProblemKind.Unreadable, path, "That meeting folder holds no record."));
                continue;
            }

            var (stored, trouble) = Read<StoredMeeting>(file);
            if (trouble is not null)
                problems.Add(trouble);
            else
                meetings.Add(stored!.ToRecord(workspaceId));
        }

        return new MeetingListing(meetings, problems);
    }

    public MeetingResult CreateMeeting(string workspaceId, string title)
    {
        if (string.IsNullOrWhiteSpace(title))
            return new MeetingResult(null, new StoreProblem(
                StoreProblemKind.Invalid, workspaceId, "A meeting needs a title."));

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
        if (string.IsNullOrWhiteSpace(meeting.Id))
            return new MeetingResult(null, new StoreProblem(
                StoreProblemKind.Invalid, meeting.WorkspaceId, "A meeting needs an id."));
        if (string.IsNullOrWhiteSpace(meeting.Title))
            return new MeetingResult(null, new StoreProblem(
                StoreProblemKind.Invalid, meeting.Id, "A meeting needs a title."));

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
            return new MeetingResult(null, new StoreProblem(
                StoreProblemKind.Invalid, meetingId, "A meeting needs a title."));

        var (existing, problem) = ReadMeeting(workspaceId, meetingId);
        return problem is not null
            ? new MeetingResult(null, problem)
            : SaveMeeting(existing! with { Title = title.Trim() });
    }

    public StoreProblem? DeleteMeeting(string workspaceId, string meetingId)
    {
        var folder = MeetingFolder(workspaceId, meetingId, out var problem);
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
        MeetingFolder(workspaceId, meetingId, out _);

    private string? MeetingFolder(string workspaceId, string meetingId, out StoreProblem? problem)
    {
        if (string.IsNullOrWhiteSpace(meetingId))
        {
            problem = new StoreProblem(StoreProblemKind.Invalid, workspaceId, "A meeting needs an id.");
            return null;
        }

        var (workspace, trouble) = Locate(workspaceId);
        problem = trouble;
        return trouble is null ? FolderFor(workspace!, meetingId) : null;
    }

    private (MeetingRecord? Meeting, StoreProblem? Problem) ReadMeeting(string workspaceId, string meetingId)
    {
        var folder = MeetingFolder(workspaceId, meetingId, out var problem);
        if (problem is not null)
            return (null, problem);

        var file = Path.Combine(folder!, MeetingFileName);
        if (!File.Exists(file))
            return (null, NoSuchMeeting(meetingId));

        var (stored, trouble) = Read<StoredMeeting>(file);
        return trouble is not null ? (null, trouble) : (stored!.ToRecord(workspaceId), null);
    }

    private static string FolderFor(Workspace workspace, string meetingId) =>
        Path.Combine(workspace.RootPath, MeetingsFolderName, meetingId);

    private (Workspace? Workspace, StoreProblem? Problem) Locate(string workspaceId)
    {
        var (entries, problem) = ReadRegistry();
        if (problem is not null)
            return (null, problem);

        var entry = entries.FirstOrDefault(e => e.Id == workspaceId);
        if (entry is null)
            return (null, NoSuchWorkspace(workspaceId));

        return Directory.Exists(entry.RootPath)
            ? (entry.ToWorkspace(), null)
            : (null, Unplugged(entry));
    }

    private (List<StoredRegistryEntry> Entries, StoreProblem? Problem) ReadRegistry()
    {
        if (!File.Exists(_registryPath))
            return ([], null);

        var (registry, problem) = Read<StoredRegistry>(_registryPath);
        return problem is not null ? ([], problem) : (registry!.Workspaces!, null);
    }

    private void WriteRegistry(List<StoredRegistryEntry> entries)
    {
        var folder = Path.GetDirectoryName(_registryPath);
        if (!string.IsNullOrEmpty(folder))
            Directory.CreateDirectory(folder);

        File.WriteAllText(
            _registryPath,
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
            entries[index] = StoredRegistryEntry.From(workspace);
        else
            entries.Add(StoredRegistryEntry.From(workspace));

        try
        {
            WriteRegistry(entries);
        }
        catch (Exception ex)
        {
            return new WorkspaceResult(null, Trouble(_registryPath, "The workspace list could not be written.", ex));
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

        // Well-formed JSON is not a well-formed record: the deserializer fills a field it cannot
        // find with null, and a record with no id names a folder no operation can open again.
        return value.Complete
            ? (value, null)
            : (null, new StoreProblem(StoreProblemKind.Unreadable, path, "The record is missing fields."));
    }

    private static string NewId() => Guid.NewGuid().ToString("N")[..16];

    private static StoreProblem NoSuchWorkspace(string id) =>
        new(StoreProblemKind.NotFound, id, "No such workspace.");

    private static StoreProblem NoSuchMeeting(string id) =>
        new(StoreProblemKind.NotFound, id, "No such meeting.");

    private static StoreProblem Unplugged(StoredRegistryEntry entry) =>
        new(StoreProblemKind.FolderMissing, entry.RootPath!,
            $"The folder for workspace \"{entry.Name}\" is not there.");

    private static WorkspaceResult Refused(string path, string detail) =>
        new(null, new StoreProblem(StoreProblemKind.Invalid, path, detail));

    private static StoreProblem Trouble(string path, string detail, Exception cause)
    {
        Log.Warning(LogCategory, $"{detail} ({path})", cause);
        return new StoreProblem(StoreProblemKind.Unwritable, path, $"{detail} {cause.Message}");
    }

    private readonly string _registryPath = registryPath;

    private interface IStoredRecord
    {
        int SchemaVersion { get; }

        bool Complete { get; }
    }

    private sealed record StoredRegistry(int SchemaVersion, List<StoredRegistryEntry>? Workspaces)
        : IStoredRecord
    {
        public bool Complete => Workspaces is not null && Workspaces.TrueForAll(w => w.Complete);
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
        public bool Complete => !string.IsNullOrWhiteSpace(Id) && !string.IsNullOrWhiteSpace(Title);

        internal static StoredMeeting From(MeetingRecord m) =>
            new(WorkspaceStore.SchemaVersion, m.Id, m.Title, m.CreatedAt, m.StartedAt, m.EndedAt,
                [.. m.Languages], m.TranscriptFileName, m.AudioFileName);

        internal MeetingRecord ToRecord(string workspaceId) =>
            new(Id!, workspaceId, Title!, CreatedAt, StartedAt, EndedAt,
                Languages ?? [], TranscriptFileName, AudioFileName);
    }
}
