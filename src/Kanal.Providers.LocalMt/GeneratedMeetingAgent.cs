using System.Text.RegularExpressions;
using Kanal.Core.Insights;

namespace Kanal.Providers.LocalMt;

public sealed partial class GeneratedMeetingAgent(ITextGenerator generator) : IMeetingAgent
{
    [GeneratedRegex(@"^(?:[-*•]|\d+[.)])\s*")]
    private static partial Regex ListMarker();

    public string Id => "local-model";

    public MeetingAgentCapabilities Caps { get; } =
        new(new HashSet<InsightKind> { InsightKind.Point, InsightKind.Proposal, InsightKind.Decision });

    public async Task<IReadOnlyList<MeetingInsight>> ProposeAsync(MeetingAgentWindow window, CancellationToken ct)
    {
        var raw = await generator.GenerateAsync(Prompt(window), ct).ConfigureAwait(false);
        return MtOutputCleaner.Clean(raw)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(line => Parse(line, window))
            .OfType<MeetingInsight>()
            .ToList();
    }

    private static MeetingInsight? Parse(string line, MeetingAgentWindow window)
    {
        var parts = ListMarker().Replace(line, "").Split('|', StringSplitOptions.TrimEntries);
        if (parts.Length != 4 || parts[1].Length == 0 || parts[2].Length == 0)
            return null;

        InsightKind? kind = parts[0].ToUpperInvariant() switch
        {
            "POINT" => InsightKind.Point,
            "PROPOSAL" => InsightKind.Proposal,
            "DECISION" => InsightKind.Decision,
            _ => null,
        };
        // Real ids are 32-hex Guids or Gladia ids; a small model copies a line number far more reliably.
        var sources = parts[3].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(number => int.TryParse(number.Trim('[', ']', '#'), out var n) ? n : 0)
            .Where(n => n >= 1 && n <= window.Finals.Count)
            .Select(n => window.Finals[n - 1].Id)
            .Distinct()
            .ToList();
        return kind is null || sources.Count == 0
            ? null
            : new MeetingInsight("", kind.Value, parts[1], parts[2], sources);
    }

    private static string Prompt(MeetingAgentWindow window) =>
        "You follow a meeting and note what matters. Below are new transcript lines, each with its number.\n" +
        "Write one line per item, in the form KIND|TOPIC|TEXT|IDS\n" +
        "KIND is POINT, PROPOSAL or DECISION. TOPIC is two to four words. " +
        $"TEXT is one short sentence in the language \"{window.ReplyLanguage}\". " +
        "IDS are the numbers of the lines that support the item, separated by commas.\n" +
        (window.KnownTopics.Count == 0
            ? ""
            : $"Reuse one of these topics when it fits: {string.Join("; ", window.KnownTopics)}\n") +
        "Write NONE if nothing matters. Output nothing else.\n\n" +
        string.Join("\n", window.Finals.Select((u, i) => $"[{i + 1}] {u.SrcLang}: {u.SrcText}"));
}
