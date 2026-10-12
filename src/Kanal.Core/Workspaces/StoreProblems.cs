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

    public static StoreProblem NotAMeetingId(string id) =>
        new(StoreProblemKind.Invalid, id, "That is not a meeting id.");

    public static StoreProblem Unreadable(string path, string detail) =>
        new(StoreProblemKind.Unreadable, path, detail);

    public static StoreProblem Unwritable(string path, string detail) =>
        new(StoreProblemKind.Unwritable, path, detail);

    public static StoreProblem Unwritable(string path, string detail, Exception cause) =>
        Unwritable(path, $"{detail} {cause.Message}");
}
