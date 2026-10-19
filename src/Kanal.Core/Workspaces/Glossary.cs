namespace Kanal.Core.Workspaces;

public sealed record GlossaryEntry(string Zh, string De, string Pl, string En, bool DoNotTranslate = false);

public sealed record GlossaryResult(IReadOnlyList<GlossaryEntry>? Entries, StoreProblem? Problem);
