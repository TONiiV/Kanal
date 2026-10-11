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

    public static StoreProblem NotAMeetingId(string id) =>
        new(StoreProblemKind.Invalid, id, "That is not a meeting id.");

    public static StoreProblem Unreadable(string path, string detail) =>
        new(StoreProblemKind.Unreadable, path, detail);

    public static StoreProblem EmptyFile(string path) =>
        new(StoreProblemKind.Unreadable, path, "The file is empty.");

    public static StoreProblem IncompleteRecord(string path) =>
        new(StoreProblemKind.Unreadable, path, "The record is missing fields.");

    public static StoreProblem IncompleteRow(string registryPath) =>
        new(StoreProblemKind.Unreadable, registryPath, "A row of the list is missing fields.");

    public static StoreProblem NoRecord(string meetingFolder) =>
        new(StoreProblemKind.Unreadable, meetingFolder, "That meeting folder holds no record.");

    public static StoreProblem NotABundle(string bundlePath) =>
        new(StoreProblemKind.Unreadable, bundlePath, "That file is not a Kanal meeting bundle.");

    public static StoreProblem IncompleteManifest(string bundlePath) =>
        new(StoreProblemKind.Unreadable, bundlePath, "The bundle's manifest is missing fields.");

    public static StoreProblem UnsupportedWorkspaceSchema(string path, int version) =>
        new(StoreProblemKind.UnsupportedVersion, path, $"Unsupported workspace schema {version}.");

    public static StoreProblem UnsupportedBundleSchema(string bundlePath, int version) =>
        new(StoreProblemKind.UnsupportedVersion, bundlePath, $"Unsupported bundle schema {version}.");

    public static StoreProblem FolderMissing(string path) =>
        new(StoreProblemKind.FolderMissing, path, "That folder is not there.");

    public static StoreProblem MeetingsFolderMissing(string folder) =>
        new(StoreProblemKind.FolderMissing, folder, "The meetings folder is not there.");

    public static StoreProblem TitleTaken(string title) =>
        new(StoreProblemKind.TitleTaken, title,
            $"Another meeting in this workspace is already called \"{title}\".");

    public static StoreProblem WrongIconType(string imagePath) =>
        new(StoreProblemKind.WrongType, imagePath,
            "A project icon must be a PNG, JPEG, ICO, BMP or WebP image.");

    public static StoreProblem NoSuchImage(string imagePath) =>
        new(StoreProblemKind.NotFound, imagePath, "That image is not there.");

    public static StoreProblem IconTooLarge(string imagePath) =>
        new(StoreProblemKind.TooLarge, imagePath, "A project icon must be 2 MB or smaller.");

    public static StoreProblem Unwritten(string path, string detail) =>
        new(StoreProblemKind.Unwritable, path, detail);

    public static StoreProblem Unwritable(string path, string detail, Exception cause)
    {
        Log.Warning(LogCategory, $"{detail} ({path})", cause);
        return Unwritten(path, $"{detail} {cause.Message}");
    }
}
