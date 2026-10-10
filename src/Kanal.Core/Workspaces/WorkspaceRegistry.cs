namespace Kanal.Core.Workspaces;

internal sealed class WorkspaceRegistry(string path)
{
    public string FilePath => path;

    public (List<StoredRegistryEntry> Entries, StoreProblem? Problem) Read()
    {
        if (!File.Exists(path) && !Directory.Exists(path))
            return ([], null);

        var (registry, problem) = RecordFile.Read<StoredRegistry>(path);
        return problem is not null ? ([], problem) : (registry!.Workspaces!, null);
    }

    public void Write(List<StoredRegistryEntry> entries)
    {
        var folder = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(folder))
            Directory.CreateDirectory(folder);

        RecordFile.Write(path, new StoredRegistry(RecordFile.SchemaVersion, entries));
    }

    public (Workspace? Workspace, StoreProblem? Problem) Locate(string workspaceId)
    {
        var (entries, problem) = Read();
        if (problem is not null)
            return (null, problem);

        var entry = entries.FirstOrDefault(e => e.Id == workspaceId && e.Complete);
        if (entry is null)
            return (null, StoreProblems.NoSuchWorkspace(workspaceId));

        return Directory.Exists(entry.RootPath)
            ? (entry.ToWorkspace(), null)
            : (null, StoreProblems.Unplugged(entry));
    }

    public StoreProblem? RefuseTaken(string rootPath)
    {
        var (entries, problem) = Read();
        return problem ?? Occupied(entries, rootPath, byAnyoneBut: null);
    }

    public WorkspaceResult Register(Workspace workspace)
    {
        var (entries, problem) = Read();
        if (problem is not null)
            return new WorkspaceResult(null, problem);

        if (Occupied(entries, workspace.RootPath, byAnyoneBut: workspace.Id) is { } taken)
            return new WorkspaceResult(null, taken);

        var index = entries.FindIndex(e => e.Id == workspace.Id);
        if (index >= 0)
        {
            var listed = entries[index];

            // Two folders, one identity: a restored backup, a share mounted twice. Only a copy
            // while the folder already listed is still there — otherwise it is the same one, moved.
            if (!StorePaths.SamePath(listed.RootPath!, workspace.RootPath) && Directory.Exists(listed.RootPath))
                return new WorkspaceResult(null, StoreProblems.Invalid(
                    workspace.RootPath,
                    $"That folder is a copy of the workspace \"{listed.Name}\", " +
                    $"which is open at {listed.RootPath}."));

            // The list holds the name the operator last gave it. A rename made while the drive was
            // out could not reach the marker file, and must not be undone by reading it back.
            workspace = workspace with
            {
                Name = listed.Name!,
                IconGlyph = WorkspaceIcons.GlyphOrNull(listed.IconGlyph),
                IconFile = WorkspaceIcons.FileOrNull(listed.IconFile),
            };
            entries[index] = StoredRegistryEntry.From(workspace);
        }
        else
        {
            entries.Add(StoredRegistryEntry.From(workspace));
        }

        return WriteOrProblem(entries) is { } unwritable
            ? new WorkspaceResult(null, unwritable)
            : new WorkspaceResult(workspace, null);
    }

    // Leaves every file where it is: dropping a row from a list is not deleting a year of meetings.
    public StoreProblem? Forget(string id)
    {
        var (entries, problem) = Read();
        if (problem is not null)
            return problem;
        if (entries.RemoveAll(e => e.Id == id) == 0)
            return StoreProblems.NoSuchWorkspace(id);

        return WriteOrProblem(entries);
    }

    private StoreProblem? WriteOrProblem(List<StoredRegistryEntry> entries)
    {
        try
        {
            Write(entries);
            return null;
        }
        catch (Exception ex)
        {
            return StoreProblems.Unwritable(path, "The workspace list could not be written.", ex);
        }
    }

    private static StoreProblem? Occupied(
        List<StoredRegistryEntry> entries, string rootPath, string? byAnyoneBut) =>
        entries.FirstOrDefault(e => e.Id != byAnyoneBut && StorePaths.SamePath(e.RootPath ?? "", rootPath))
                is { } already
            ? StoreProblems.Invalid(rootPath, $"That folder is already the workspace \"{already.Name}\".")
            : null;
}
