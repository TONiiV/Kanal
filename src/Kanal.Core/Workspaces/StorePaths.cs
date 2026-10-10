namespace Kanal.Core.Workspaces;

internal static class StorePaths
{
    // Linux tells Acme and acme apart; macOS and Windows do not, and neither may this.
    public static readonly StringComparison NameComparison = OperatingSystem.IsLinux()
        ? StringComparison.Ordinal
        : StringComparison.OrdinalIgnoreCase;

    public static string NewId() => Guid.NewGuid().ToString("N")[..16];

    public static bool IsFolderName(string? id) =>
        !string.IsNullOrEmpty(id) && id.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');

    // A name, never a path: these are resolved against the meeting's own folder.
    public static bool IsFileName(string? name) =>
        name is null
        || (!string.IsNullOrWhiteSpace(name)
            && !name.Contains('/') && !name.Contains('\\')
            && name is not ("." or ".."));

    public static bool IsArtifactPath(string? path, string meetingFolder)
    {
        if (path is null)
            return true;

        try
        {
            if (!Path.IsPathFullyQualified(path) || !IsFileName(Path.GetFileName(path)))
                return false;

            var parent = Path.GetDirectoryName(Path.GetFullPath(path));
            return parent is not null && SamePath(parent, meetingFolder);
        }
        catch (Exception)
        {
            return false;
        }
    }

    public static bool SamePath(string a, string b) => string.Equals(a, b, NameComparison);

    // Two spellings of one folder must not become two workspaces, so every link on the way down
    // is resolved: on macOS /tmp and /var are themselves symlinks, which makes this the usual case.
    public static string Canonical(string path)
    {
        var full = path;
        try
        {
            full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
            for (var hops = 0; hops < 40; hops++)
            {
                var (resolved, followed) = FollowFirstLink(full);
                if (!followed)
                    return resolved;

                full = resolved;
            }
        }
        catch (Exception)
        {
            // unreachable or malformed; the folder checks phrase it, and the absolute form stands
        }

        return full;
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

            // A folder about to be created has nothing below it to resolve, and asking would throw.
            if (!Directory.Exists(walked))
                break;

            if (new DirectoryInfo(walked).ResolveLinkTarget(returnFinalTarget: true) is not { } target)
                continue;

            var rest = Path.Combine([.. parts[(i + 1)..]]);
            return (Path.TrimEndingDirectorySeparator(
                Path.GetFullPath(Path.Combine(target.FullName, rest))), true);
        }

        return (full, false);
    }
}
