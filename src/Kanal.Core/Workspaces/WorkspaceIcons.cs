namespace Kanal.Core.Workspaces;

internal static class WorkspaceIcons
{
    public const string FileStem = "kanal-icon";
    public const long MaxBytes = 2 * 1024 * 1024;
    public static readonly IReadOnlyList<string> Extensions = [".png", ".jpg", ".jpeg", ".ico", ".bmp", ".webp"];

    public static StoreProblem? Refusal(string imagePath)
    {
        if (!Extensions.Contains(ExtensionOf(imagePath)))
            return new StoreProblem(StoreProblemKind.WrongType, imagePath,
                "A project icon must be a PNG, JPEG, ICO, BMP or WebP image.");
        if (!File.Exists(imagePath))
            return new StoreProblem(StoreProblemKind.NotFound, imagePath, "That image is not there.");
        if (new FileInfo(imagePath).Length > MaxBytes)
            return new StoreProblem(StoreProblemKind.TooLarge, imagePath, "A project icon must be 2 MB or smaller.");

        return null;
    }

    public static string CopyInto(string root, string imagePath)
    {
        var name = FileStem + ExtensionOf(imagePath);
        var target = Path.Combine(root, name);
        var source = Path.Combine(
            StorePaths.Canonical(Path.GetDirectoryName(Path.GetFullPath(imagePath))!),
            Path.GetFileName(imagePath));
        if (!StorePaths.SamePath(source, target))
            File.Copy(imagePath, target, overwrite: true);
        DeleteFiles(root, except: name);
        return name;
    }

    public static void DeleteFiles(string root, string? except = null)
    {
        if (!Directory.Exists(root))
            return;

        foreach (var name in Extensions.Select(extension => FileStem + extension))
            if (!string.Equals(name, except, StorePaths.NameComparison))
                File.Delete(Path.Combine(root, name));
    }

    // An unknown or unsafe icon reads as the default icon, never as a broken row.
    public static string? GlyphOrNull(string? glyph) => StorePaths.IsFolderName(glyph) ? glyph : null;

    public static string? FileOrNull(string? name) =>
        Extensions.Any(ext => string.Equals(name, FileStem + ext, StringComparison.Ordinal))
            ? name
            : null;

    private static string ExtensionOf(string imagePath) => Path.GetExtension(imagePath).ToLowerInvariant();
}
