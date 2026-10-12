using Kanal.Core.Diagnostics;

namespace Kanal.Core.Workspaces;

public sealed class WorkspaceStore
{
    public const int SchemaVersion = RecordFile.SchemaVersion;
    public const string WorkspaceFileName = "kanal-workspace.json";
    public const string MeetingFileName = MeetingRecords.FileName;
    public const string AudioFileName = "audio.wav";
    public const string IconFileStem = WorkspaceIcons.FileStem;
    public const long MaxIconBytes = WorkspaceIcons.MaxBytes;
    public static readonly IReadOnlyList<string> IconExtensions = WorkspaceIcons.Extensions;
    private const string NeedsName = "A workspace needs a name.";

    private readonly WorkspaceRegistry registry;
    private readonly MeetingRecords meetings;

    public WorkspaceStore(string registryPath)
    {
        registry = new WorkspaceRegistry(registryPath);
        meetings = new MeetingRecords(registry);
    }

    public WorkspaceListing ListWorkspaces()
    {
        var (entries, problem) = registry.Read();
        if (problem is not null)
            return new WorkspaceListing([], [problem]);

        var workspaces = new List<Workspace>();
        var problems = new List<StoreProblem>();
        foreach (var entry in entries)
        {
            if (!entry.Complete)
            {
                problems.Add(new StoreProblem(
                    StoreProblemKind.RegistryRowDamaged, registry.FilePath, "A row of the list is missing fields."));
                continue;
            }

            workspaces.Add(entry.ToWorkspace());
            if (!Directory.Exists(entry.RootPath))
                problems.Add(StoreProblems.Unplugged(entry));
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

        rootPath = StorePaths.Canonical(rootPath);

        // Adopted under its own name, never overwritten: picking last year's folder means "open this".
        if (Directory.Exists(rootPath) && File.Exists(MarkerIn(rootPath)))
            return OpenWorkspace(rootPath);

        // Checked before anything is written: the marker file below would otherwise be replaced
        // on the way to a refusal, taking the identity of the workspace already living there.
        if (registry.RefuseTaken(rootPath) is { } taken)
            return new WorkspaceResult(null, taken);

        var workspace = new Workspace(StorePaths.NewId(), name.Trim(), rootPath, DateTimeOffset.UtcNow);
        try
        {
            Directory.CreateDirectory(MeetingRecords.FolderIn(rootPath));
            WriteMarker(workspace);
        }
        catch (Exception ex)
        {
            const string failure = "The workspace folder could not be written.";
            Log.Warning(StoreProblems.LogCategory, $"{failure} ({rootPath})", ex);
            return new WorkspaceResult(null, StoreProblems.Unwritable(rootPath, failure, ex));
        }

        return registry.Register(workspace);
    }

    public WorkspaceResult OpenWorkspace(string rootPath)
    {
        rootPath = StorePaths.Canonical(rootPath);
        if (!Directory.Exists(rootPath))
            return new WorkspaceResult(null, new StoreProblem(
                StoreProblemKind.FolderMissing, rootPath, "That folder is not there."));

        var path = MarkerIn(rootPath);
        if (!File.Exists(path))
            return Refused(rootPath, "That folder does not hold a workspace.");

        var (stored, problem) = RecordFile.Read<StoredWorkspace>(path);
        return problem is not null
            ? new WorkspaceResult(null, problem)
            : registry.Register(stored!.ToWorkspace(rootPath));
    }

    public WorkspaceResult RenameWorkspace(string id, string name) =>
        string.IsNullOrWhiteSpace(name)
            ? Refused(registry.FilePath, NeedsName)
            : Rewrite(id, "The rename could not be written.", w => w with { Name = name.Trim() });

    public WorkspaceResult SetWorkspaceIcon(string id, string glyph) =>
        StorePaths.IsFolderName(glyph)
            ? Rewrite(id, "The icon could not be written.", w =>
            {
                WorkspaceIcons.DeleteFiles(w.RootPath);
                return w with { IconGlyph = glyph, IconFile = null };
            })
            : Refused(glyph, "That is not an icon name.");

    public WorkspaceResult SetWorkspaceIconFile(string id, string imagePath)
    {
        if (WorkspaceIcons.Refusal(imagePath) is { } refusal)
            return new WorkspaceResult(null, refusal);

        var (_, problem) = registry.Locate(id);
        if (problem is not null)
            return new WorkspaceResult(null, problem);

        return Rewrite(id, "The icon could not be copied.", w =>
            w with { IconGlyph = null, IconFile = WorkspaceIcons.CopyInto(w.RootPath, imagePath) });
    }

    public WorkspaceResult ResetWorkspaceIcon(string id) =>
        Rewrite(id, "The icon could not be reset.", w =>
        {
            WorkspaceIcons.DeleteFiles(w.RootPath);
            return w with { IconGlyph = null, IconFile = null };
        });

    // Only what Kanal wrote: the operator picked this folder, and it may hold anything else.
    public StoreProblem? DeleteWorkspace(string id)
    {
        var (workspace, problem) = registry.Locate(id);
        if (problem is not null)
            return problem;

        var root = workspace!.RootPath;
        var marker = MarkerIn(root);
        if (!File.Exists(marker))
            return StoreProblems.NotThisWorkspace(root, "That folder does not hold this workspace.");

        var (stored, unreadable) = RecordFile.Read<StoredWorkspace>(marker);
        if (unreadable is not null)
            return unreadable;
        if (stored!.Id != id)
            return StoreProblems.NotThisWorkspace(root, "That folder holds another workspace.");

        try
        {
            var meetingsFolder = MeetingRecords.FolderIn(root);
            if (Directory.Exists(meetingsFolder))
                Directory.Delete(meetingsFolder, recursive: true);
            WorkspaceIcons.DeleteFiles(root);
            File.Delete(marker);
            if (!Directory.EnumerateFileSystemEntries(root).Any())
                Directory.Delete(root);
        }
        catch (Exception ex)
        {
            const string failure = "The workspace files could not be deleted.";
            Log.Warning(StoreProblems.LogCategory, $"{failure} ({root})", ex);
            return StoreProblems.Unwritable(root, failure, ex);
        }

        return ForgetWorkspace(id);
    }

    public StoreProblem? ForgetWorkspace(string id) => registry.Forget(id);

    // The marker is written before the list: a refusal or a failed write leaves the old row in place.
    public WorkspaceResult RelocateWorkspace(string id, string newRootPath)
    {
        if (string.IsNullOrWhiteSpace(newRootPath))
            return Refused(newRootPath, "A workspace needs a folder.");

        newRootPath = StorePaths.Canonical(newRootPath);
        if (!Directory.Exists(newRootPath))
            return new WorkspaceResult(null, new StoreProblem(
                StoreProblemKind.FolderMissing, newRootPath, "That folder is not there."));

        var (entries, problem) = registry.Read();
        if (problem is not null)
            return new WorkspaceResult(null, problem);

        var index = entries.FindIndex(e => e.Id == id && e.Complete);
        if (index < 0)
            return new WorkspaceResult(null, StoreProblems.NoSuchWorkspace(id));
        if (registry.RefuseTaken(newRootPath, exceptId: id) is { } taken)
            return new WorkspaceResult(null, taken);

        var current = entries[index];
        if (!StorePaths.SamePath(current.RootPath!, newRootPath) && Directory.Exists(current.RootPath))
            return Refused(newRootPath, $"The workspace \"{current.Name}\" is still at {current.RootPath}.");

        var relocated = current.ToWorkspace() with { RootPath = newRootPath };
        var marker = MarkerIn(newRootPath);
        if (File.Exists(marker))
        {
            var (stored, unreadable) = RecordFile.Read<StoredWorkspace>(marker);
            if (unreadable is not null)
                return new WorkspaceResult(null, unreadable);
            if (stored!.Id != id)
                return new WorkspaceResult(null, StoreProblems.NotThisWorkspace(
                    newRootPath, "That folder holds another workspace."));
        }

        try
        {
            if (!File.Exists(marker))
            {
                Directory.CreateDirectory(MeetingRecords.FolderIn(newRootPath));
                WriteMarker(relocated);
            }

            entries[index] = StoredRegistryEntry.From(relocated);
            registry.Write(entries);
        }
        catch (Exception ex)
        {
            const string failure = "The workspace could not be moved.";
            Log.Warning(StoreProblems.LogCategory, $"{failure} ({newRootPath})", ex);
            return new WorkspaceResult(null, StoreProblems.Unwritable(newRootPath, failure, ex));
        }

        return new WorkspaceResult(relocated, null);
    }

    public StoreProblem? CreateMeetingsFolder(string id)
    {
        var (workspace, problem) = registry.Locate(id);
        if (problem is not null)
            return problem;

        var folder = MeetingRecords.FolderIn(workspace!.RootPath);
        if (Directory.Exists(folder))
            return null;

        try
        {
            Directory.CreateDirectory(folder);
            return null;
        }
        catch (Exception ex)
        {
            const string failure = "The meetings folder could not be created.";
            Log.Warning(StoreProblems.LogCategory, $"{failure} ({folder})", ex);
            return StoreProblems.Unwritable(folder, failure, ex);
        }
    }

    public StoreProblem? DropBrokenRegistryRows() => registry.DropBroken();

    /// <summary>
    /// The workspace the host records into, created on the first launch. A meeting with nowhere
    /// to be written is the one thing the operator cannot recover from afterwards.
    /// </summary>
    public WorkspaceResult EnsureWorkspace(string name, string rootPath)
    {
        var listing = ListWorkspaces();
        return listing.Workspaces.Count > 0
            ? new WorkspaceResult(listing.Workspaces[0], null)
            : CreateWorkspace(name, rootPath);
    }

    public MeetingListing ListMeetings(string workspaceId) => meetings.List(workspaceId);

    public MeetingResult CreateMeeting(string workspaceId, string title) => meetings.Create(workspaceId, title);

    public MeetingResult SaveMeeting(MeetingRecord meeting) => meetings.Save(meeting);

    public MeetingResult RenameMeeting(string workspaceId, string meetingId, string title) =>
        meetings.Rename(workspaceId, meetingId, title);

    /// <summary>
    /// The wanted title, or the next free number after it. A model-generated name arrives
    /// mid-meeting and has nobody to ask, so it is numbered rather than refused.
    /// </summary>
    public string FreeTitle(string workspaceId, string title, string? exceptMeetingId) =>
        meetings.FreeTitle(workspaceId, title, exceptMeetingId);

    public StoreProblem? DeleteMeeting(string workspaceId, string meetingId) =>
        meetings.Delete(workspaceId, meetingId);

    public RecoverResult RecoverMeeting(string workspaceId, string folder, string baseTitle) =>
        meetings.Recover(workspaceId, folder, baseTitle);

    public RecoverResult ReidentifyMeeting(string workspaceId, string folder) =>
        meetings.Reidentify(workspaceId, folder);

    public string? MeetingFolder(string workspaceId, string meetingId) =>
        meetings.FolderOf(workspaceId, meetingId).Folder;

    private WorkspaceResult Rewrite(string id, string failure, Func<Workspace, Workspace> change)
    {
        var (entries, problem) = registry.Read();
        if (problem is not null)
            return new WorkspaceResult(null, problem);

        var index = entries.FindIndex(e => e.Id == id && e.Complete);
        if (index < 0)
            return new WorkspaceResult(null, StoreProblems.NoSuchWorkspace(id));

        var current = entries[index].ToWorkspace();
        Workspace changed;
        try
        {
            changed = change(current);
            entries[index] = StoredRegistryEntry.From(changed);
            registry.Write(entries);
            if (Directory.Exists(changed.RootPath))
                WriteMarker(changed);
        }
        catch (Exception ex)
        {
            Log.Warning(StoreProblems.LogCategory, $"{failure} ({current.RootPath})", ex);
            return new WorkspaceResult(null, StoreProblems.Unwritable(current.RootPath, failure, ex));
        }

        return new WorkspaceResult(changed, null);
    }

    private static string MarkerIn(string rootPath) => Path.Combine(rootPath, WorkspaceFileName);

    private static void WriteMarker(Workspace workspace) =>
        RecordFile.Write(MarkerIn(workspace.RootPath), StoredWorkspace.From(workspace));

    private static WorkspaceResult Refused(string subject, string detail) =>
        new(null, StoreProblems.Invalid(subject, detail));
}
