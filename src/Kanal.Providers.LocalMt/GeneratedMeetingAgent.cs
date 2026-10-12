using Kanal.Core.Insights;

namespace Kanal.Providers.LocalMt;

public sealed class GeneratedMeetingAgent(ITextGenerator generator) : IMeetingAgent
{
    public string Id => "local-model";

    public MeetingAgentCapabilities Caps { get; } =
        new(new HashSet<InsightKind> { InsightKind.Point, InsightKind.Proposal, InsightKind.Decision });

    public async Task<IReadOnlyList<MeetingInsight>> ProposeAsync(MeetingAgentWindow window, CancellationToken ct)
    {
        var raw = await generator.GenerateAsync(Prompt(window), ct).ConfigureAwait(false);
        return MtOutputCleaner.Clean(raw)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(Parse)
            .OfType<MeetingInsight>()
            .ToList();
    }

    private static MeetingInsight? Parse(string line)
    {
        var parts = line.Split('|', StringSplitOptions.TrimEntries);
        if (parts.Length != 4 || parts[1].Length == 0 || parts[2].Length == 0)
            return null;

        InsightKind? kind = parts[0].ToUpperInvariant() switch
        {
            "POINT" => InsightKind.Point,
            "PROPOSAL" => InsightKind.Proposal,
            "DECISION" => InsightKind.Decision,
            _ => null,
        };
        var sources = parts[3].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return kind is null || sources.Length == 0
            ? null
            : new MeetingInsight("", kind.Value, parts[1], parts[2], sources);
    }

    private static string Prompt(MeetingAgentWindow window) =>
        "You follow a meeting and note what matters. Below are new transcript lines, each with its id.\n" +
        "Write one line per item, in the form KIND|TOPIC|TEXT|IDS\n" +
        "KIND is POINT, PROPOSAL or DECISION. TOPIC is two to four words. " +
        $"TEXT is one short sentence in the language \"{window.ReplyLanguage}\". " +
        "IDS are the ids of the lines that support the item, separated by commas.\n" +
        (window.KnownTopics.Count == 0
            ? ""
            : $"Reuse one of these topics when it fits: {string.Join("; ", window.KnownTopics)}\n") +
        "Write NONE if nothing matters. Output nothing else.\n\n" +
        string.Join("\n", window.Finals.Select(u => $"[{u.Id}] {u.SrcLang}: {u.SrcText}"));
}
