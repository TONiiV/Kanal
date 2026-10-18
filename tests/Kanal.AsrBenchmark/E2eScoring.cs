namespace Kanal.AsrBenchmark;

internal sealed record E2eRun(
    string Route,
    Clip Clip,
    string Transcript,
    IReadOnlyList<string> SrcLangs,
    IReadOnlyDictionary<string, string> Translations,
    double? LatencySeconds,
    double? AsrSeconds = null,
    double? OtherLoad = null,
    bool Rerun = false,
    DateTime? RanAt = null,
    string? Machine = null,
    int Attempts = 1)
{
    public bool Failed => Transcript.Length == 0 && Translations.Count == 0;
}

internal sealed record PairScore(string Route, string Source, string Target, double ChrF, int Codes, int Strict, int Loose);

internal sealed record RouteSummary(string Route, string Source, string Target, int Pairs, double ChrF, int Codes, int Strict, int Loose);

internal sealed record LatencySummary(int N, double P50, double P95);

internal sealed record HeardUtterance(string Text, string SrcLang, double FirstAt, double LastAt, IReadOnlyDictionary<string, string> Translations);

internal sealed record RouteAResult(
    string Transcript, IReadOnlyList<string> SrcLangs, IReadOnlyDictionary<string, string> Translations, double? LatencySeconds, bool Complete);

internal sealed record FinalsSplitRow(string Route, bool Several, int Clips, double ChrF);

internal sealed record MergeResult(IReadOnlyList<E2eRun> Runs, IReadOnlyList<string> KeptEarlier);

internal sealed record LabelSummary(int Clips, int Finals, double PerClip, double CorrectPct);

internal sealed record LengthSummary(double Characters, double Words);

internal sealed record LoadSummary(int N, double Mean, double Max);

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

    public static bool IsComplete(E2eRun run) =>
        Targets(run.Clip.Lang).All(t => run.Translations.GetValueOrDefault(t, "").Length > 0);

    public static LatencySummary Latency(IEnumerable<E2eRun> runs, bool completeOnly = false)
    {
        var seconds = runs.Where(r => r.LatencySeconds is not null && (!completeOnly || IsComplete(r)))
            .Select(r => r.LatencySeconds!.Value).ToList();
        return new LatencySummary(seconds.Count, Scoring.Percentile(seconds, 0.5), Scoring.Percentile(seconds, 0.95));
    }

    public static Dictionary<string, string> Join(IEnumerable<IReadOnlyDictionary<string, string>> parts)
    {
        var list = parts.ToList();
        return Corpus.Languages
            .Select(l => (Lang: l, Text: string.Join(" ", list.Select(p => p.GetValueOrDefault(l, "")).Where(t => t.Length > 0))))
            .Where(x => x.Text.Length > 0)
            .ToDictionary(x => x.Lang, x => x.Text);
    }

    // Gladia closes the session 1 s after the clip, so a late translation can be lost: such a clip gets no latency.
    public static RouteAResult RouteA(IEnumerable<HeardUtterance> heard, double speechEndAt)
    {
        var spoken = heard.Where(u => u.Text.Trim().Length > 0).OrderBy(u => u.FirstAt).ToList();
        var complete = spoken.Count > 0 && spoken.All(u => Corpus.Languages
            .Where(l => !string.Equals(l, u.SrcLang, StringComparison.OrdinalIgnoreCase))
            .All(l => u.Translations.GetValueOrDefault(l, "").Length > 0));
        return new RouteAResult(
            string.Join(" ", spoken.Select(u => u.Text.Trim())),
            spoken.Select(u => u.SrcLang).ToList(),
            Join(spoken.Select(u => u.Translations)),
            complete ? spoken.Max(u => u.LastAt) - speechEndAt : null,
            complete);
    }

    public static MergeResult Merge(IReadOnlyList<E2eRun> earlier, IReadOnlyList<E2eRun> fresh)
    {
        var runs = earlier.ToList();
        var kept = new List<string>();
        foreach (var run in fresh)
        {
            var index = runs.FindIndex(r => r.Route == run.Route && r.Clip.Id == run.Clip.Id);
            if (index < 0)
                runs.Add(run);
            else if (run.Failed)
                kept.Add($"{run.Route} {run.Clip.Id}");
            else
                runs[index] = run with { Rerun = true };
        }

        return new MergeResult(runs, kept);
    }

    public static IReadOnlyList<FinalsSplitRow> FinalsSplit(IEnumerable<E2eRun> runs) =>
        runs.GroupBy(r => (r.Route, Several: r.SrcLangs.Count > 1))
            .OrderBy(g => g.Key.Route, StringComparer.Ordinal).ThenBy(g => g.Key.Several)
            .Select(g => new FinalsSplitRow(g.Key.Route, g.Key.Several, g.Count(), g.SelectMany(Pairs).Average(p => p.ChrF)))
            .ToList();

    public static IReadOnlyList<string> NotRerun(IReadOnlyList<string> only, IEnumerable<E2eRun> fresh)
    {
        var ran = fresh.Select(r => r.Clip.Id).ToHashSet();
        return only.Where(id => !ran.Contains(id)).ToList();
    }

    public static IEnumerable<string> Only(IEnumerable<string> ids, IReadOnlyList<string>? only) =>
        only is null ? ids : ids.Where(id => only.Contains(id));

    public static LabelSummary Labels(IEnumerable<E2eRun> runs)
    {
        var list = runs.ToList();
        var finals = list.Sum(r => r.SrcLangs.Count);
        var correct = list.Sum(r => r.SrcLangs.Count(l => string.Equals(l, r.Clip.Lang, StringComparison.OrdinalIgnoreCase)));
        return new LabelSummary(list.Count, finals, list.Count == 0 ? 0 : (double)finals / list.Count,
            finals == 0 ? 0 : 100.0 * correct / finals);
    }

    // Words only for the languages that separate words with spaces.
    public static LengthSummary TranscriptLength(IEnumerable<E2eRun> runs)
    {
        var list = runs.ToList();
        var spaced = list.Where(r => r.Clip.Lang != "zh").ToList();
        return new LengthSummary(
            list.Count == 0 ? 0 : list.Average(r => r.Transcript.Length),
            spaced.Count == 0 ? 0 : spaced.Average(r => Scoring.Words(r.Transcript).Length));
    }

    public static LoadSummary OtherLoad(IEnumerable<E2eRun> runs)
    {
        var loads = runs.Where(r => r.OtherLoad is not null).Select(r => r.OtherLoad!.Value).ToList();
        return new LoadSummary(loads.Count, loads.Count == 0 ? 0 : loads.Average(), loads.DefaultIfEmpty().Max());
    }
}
