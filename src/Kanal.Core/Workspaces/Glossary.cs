using System.Text.Json.Serialization;

namespace Kanal.Core.Workspaces;

public sealed record GlossaryEntry(string Zh, string De, string Pl, string En, bool DoNotTranslate = false)
{
    internal bool HasTerm => !string.IsNullOrEmpty(Zh + De + Pl + En);

    internal GlossaryEntry Trimmed() => new(Zh.Trim(), De.Trim(), Pl.Trim(), En.Trim(), DoNotTranslate);
}

public sealed record GlossaryResult(IReadOnlyList<GlossaryEntry>? Entries, StoreProblem? Problem);

internal sealed record StoredGlossary(int SchemaVersion, List<StoredGlossaryEntry?>? Entries) : IStoredRecord
{
    [JsonIgnore]
    public bool Complete => Entries is not null && Entries.All(e => e is not null);

    internal static StoredGlossary From(IEnumerable<GlossaryEntry> entries) =>
        new(RecordFile.SchemaVersion, [.. entries.Select(e => new StoredGlossaryEntry(e.Zh, e.De, e.Pl, e.En, e.DoNotTranslate))]);

    internal IReadOnlyList<GlossaryEntry> ToEntries() =>
        [.. Entries!.Select(e => new GlossaryEntry(e!.Zh ?? "", e.De ?? "", e.Pl ?? "", e.En ?? "", e.DoNotTranslate))];
}

internal sealed record StoredGlossaryEntry(string? Zh, string? De, string? Pl, string? En, bool DoNotTranslate);
