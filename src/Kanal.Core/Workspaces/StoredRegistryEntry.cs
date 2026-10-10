namespace Kanal.Core.Workspaces;

internal sealed record StoredRegistryEntry(
    string? Id, string? Name, string? RootPath, DateTimeOffset CreatedAt,
    string? IconGlyph = null, string? IconFile = null)
{
    internal bool Complete =>
        StorePaths.IsFolderName(Id)
        && !string.IsNullOrWhiteSpace(Name)
        && !string.IsNullOrWhiteSpace(RootPath)
        && CreatedAt != default;

    internal static StoredRegistryEntry From(Workspace w) =>
        new(w.Id, w.Name, w.RootPath, w.CreatedAt, w.IconGlyph, w.IconFile);

    internal Workspace ToWorkspace() =>
        new(Id!, Name!, RootPath!, CreatedAt,
            WorkspaceIcons.GlyphOrNull(IconGlyph), WorkspaceIcons.FileOrNull(IconFile));
}
