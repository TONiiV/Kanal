using System.Diagnostics.CodeAnalysis;

namespace Kanal.Core.Updates;

public sealed record UpdateManifest(
    ReleaseVersion Latest,
    ReleaseVersion MinimumSupported,
    string? Notes,
    Uri? DownloadUrl)
{
    public static bool TryParse(string? json, [NotNullWhen(true)] out UpdateManifest? manifest)
    {
        manifest = null;
        return false;
    }
}
