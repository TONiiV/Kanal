namespace Kanal.AsrBenchmark;

internal sealed record E2eRun(
    string Route,
    Clip Clip,
    string Transcript,
    IReadOnlyList<string> SrcLangs,
    IReadOnlyDictionary<string, string> Translations,
    double? LatencySeconds,
    double? AsrSeconds = null);

internal sealed record PairScore(string Route, string Source, string Target, double ChrF, int Codes, int Strict, int Loose);

internal sealed record RouteSummary(string Route, string Source, string Target, int Pairs, double ChrF, int Codes, int Strict, int Loose);

internal sealed record LatencySummary(int N, double P50, double P95);

internal static class E2e
{
    public const string All = "all";

    public static int SentenceIndex(string clipId) => int.Parse(clipId.Split('-')[1]);

    public static string Reference(string clipId, string target) =>
        Corpus.Meeting.Where(m => m.Lang == target).ElementAt(SentenceIndex(clipId) - 1).Text;

    public static IReadOnlyList<string> Targets(string source) => Corpus.Languages.Where(l => l != source).ToList();

    public static IReadOnlyList<PairScore> Pairs(E2eRun run) =>
        Targets(run.Clip.Lang).Select(target =>
        {
            var reference = Reference(run.Clip.Id, target);
            var hypothesis = run.Translations.GetValueOrDefault(target, "");
            return new PairScore(run.Route, run.Clip.Lang, target, Scoring.ChrF(hypothesis, reference),
                Scoring.Codes(reference).Count,
                Scoring.CodesKept(reference, hypothesis, loose: false),
                Scoring.CodesKept(reference, hypothesis, loose: true));
        }).ToList();

    public static IReadOnlyList<RouteSummary> Summarise(IEnumerable<E2eRun> runs)
    {
        var pairs = runs.SelectMany(Pairs).ToList();
        var rows = new List<RouteSummary>();
        foreach (var route in pairs.Select(p => p.Route).Distinct())
        foreach (var source in Corpus.Languages.Append(All))
        foreach (var target in Corpus.Languages.Append(All))
        {
            var group = pairs.Where(p => p.Route == route
                                         && (source == All || p.Source == source)
                                         && (target == All || p.Target == target)).ToList();
            if (group.Count > 0)
                rows.Add(new RouteSummary(route, source, target, group.Count, group.Average(p => p.ChrF),
                    group.Sum(p => p.Codes), group.Sum(p => p.Strict), group.Sum(p => p.Loose)));
        }

        return rows;
    }

    // A CER and a WER share no unit, so the figure for several languages is the mean of the language rates.
    public static double AsrErrorPct(IEnumerable<E2eRun> runs)
    {
        var rates = runs.GroupBy(r => r.Clip.Lang).Select(g =>
        {
            var scores = g.Select(r => Scoring.Errors(g.Key, r.Transcript, r.Clip.Reference, numbers: true)).ToList();
            return 100.0 * scores.Sum(s => s.Errors) / Math.Max(1, scores.Sum(s => s.ReferenceLength));
        }).ToList();
        return rates.Count == 0 ? 0 : rates.Average();
    }

    public static LatencySummary Latency(IEnumerable<E2eRun> runs)
    {
        var seconds = runs.Where(r => r.LatencySeconds is not null).Select(r => r.LatencySeconds!.Value).ToList();
        return new LatencySummary(seconds.Count, Scoring.Percentile(seconds, 0.5), Scoring.Percentile(seconds, 0.95));
    }
}
