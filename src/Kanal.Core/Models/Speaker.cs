namespace Kanal.Core.Models;

/// <summary>
/// A diarized speaker identity. Merges are non-destructive: utterances keep their
/// original tag, and clients resolve any tag listed in MergedFrom to this speaker.
/// </summary>
public sealed record Speaker(
    string Tag,
    string? DisplayName,
    IReadOnlyList<string> MergedFrom,
    string Color);
