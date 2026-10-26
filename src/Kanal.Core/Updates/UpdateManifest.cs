using System.Diagnostics.CodeAnalysis;
using System.Text.Json;

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
        if (string.IsNullOrWhiteSpace(json))
            return false;

        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !TryVersion(root, "latest", out var latest) ||
                !TryVersion(root, "minimumSupported", out var minimum))
                return false;

            manifest = new UpdateManifest(latest, minimum, Text(root, "notes"), HttpsLink(root, "downloadUrl"));
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool TryVersion(JsonElement root, string name, out ReleaseVersion version)
    {
        version = default;
        return Text(root, name) is { } text && ReleaseVersion.TryParse(text, out version);
    }

    private static string? Text(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static Uri? HttpsLink(JsonElement root, string name) =>
        Uri.TryCreate(Text(root, name), UriKind.Absolute, out var link) &&
        link.Scheme == Uri.UriSchemeHttps
            ? link
            : null;
}
