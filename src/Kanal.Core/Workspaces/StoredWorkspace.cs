using System.Text.Json.Serialization;

namespace Kanal.Core.Workspaces;

// No path field: the folder it is in is its path, and a stale copy would outrank the truth.
internal sealed record StoredWorkspace(
    int SchemaVersion, string? Id, string? Name, DateTimeOffset CreatedAt,
    string? IconGlyph = null, string? IconFile = null) : IStoredRecord
{
    [JsonIgnore]
    public bool Complete =>
        StorePaths.IsFolderName(Id) && !string.IsNullOrWhiteSpace(Name) && CreatedAt != default;

    internal static StoredWorkspace From(Workspace w) =>
        new(RecordFile.SchemaVersion, w.Id, w.Name, w.CreatedAt, w.IconGlyph, w.IconFile);

    internal Workspace ToWorkspace(string rootPath) =>
        new(Id!, Name!, rootPath, CreatedAt,
            WorkspaceIcons.GlyphOrNull(IconGlyph), WorkspaceIcons.FileOrNull(IconFile));
}
