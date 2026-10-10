using Kanal.Core.Diagnostics;

namespace Kanal.Core.Workspaces;

internal static class StoreProblems
{
    public const string LogCategory = "workspaces";

    public static StoreProblem Invalid(string subject, string detail) =>
        new(StoreProblemKind.Invalid, subject, detail);

    public static StoreProblem NoSuchWorkspace(string id) =>
        new(StoreProblemKind.NotFound, id, "No such workspace.");

    public static StoreProblem NoSuchMeeting(string id) =>
        new(StoreProblemKind.NotFound, id, "No such meeting.");

    public static StoreProblem NotThisWorkspace(string subject, string detail) =>
        new(StoreProblemKind.NotThisWorkspace, subject, detail);

    public static StoreProblem Unplugged(StoredRegistryEntry entry) =>
        new(StoreProblemKind.FolderMissing, entry.RootPath!,
            $"The folder for workspace \"{entry.Name}\" is not there.");

    public static StoreProblem WrongFolder(string path) =>
        new(StoreProblemKind.Unreadable, path,
            "That meeting record does not belong to the folder it is in.");

    public static StoreProblem Unwritable(string path, string detail, Exception cause)
    {
        Log.Warning(LogCategory, $"{detail} ({path})", cause);
        return new StoreProblem(StoreProblemKind.Unwritable, path, $"{detail} {cause.Message}");
    }
}
