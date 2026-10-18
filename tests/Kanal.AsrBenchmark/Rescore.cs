namespace Kanal.AsrBenchmark;

internal static class Rescore
{
    public static double Pct(double part, double whole) => 100.0 * part / Math.Max(1, whole);

    public static List<ClipRow> ScoreAscendClips(IEnumerable<ClipRow> clips) =>
        clips.Select(c =>
        {
            var plain = Scoring.MixedErrors(c.Hypothesis, c.Reference);
            var numbers = Scoring.MixedErrors(c.Hypothesis, c.Reference, numbers: true);
            return c with { Errors = plain.Errors, Length = plain.ReferenceLength, NumbersErrors = numbers.Errors, NumbersLength = numbers.ReferenceLength };
        }).ToList();

    public static IEnumerable<(string Metric, double Value, string Unit)> AscendMetrics(string lang, IReadOnlyList<ClipRow> clips)
    {
        yield return ("mer_pct", Pct(clips.Sum(c => c.Errors), clips.Sum(c => c.Length)), "%");
        yield return ("mer_numbers_pct", Pct(clips.Sum(c => c.NumbersErrors), clips.Sum(c => c.NumbersLength)), "%");
        yield return ("empty_clips", clips.Count(c => c.Hypothesis.Length == 0), "n");
        yield return ("clips", clips.Count, "n");
        if (lang != "mixed")
            yield break;
        var kept = clips.Select(c => Scoring.EnglishKept(c.Hypothesis, c.Reference)).ToList();
        yield return ("english_words", kept.Sum(k => k.Total), "n");
        yield return ("english_kept_pct", Pct(kept.Sum(k => k.Kept), kept.Sum(k => k.Total)), "%");
    }

    public static (List<Row> Rows, List<ClipRow> Clips) Apply(IReadOnlyList<SavedReport> saved)
    {
        var all = saved.SelectMany(r => r.Clips).ToList();
        if (all.GroupBy(c => (c.Model, c.Provider, c.Room, c.Set, c.Id)).FirstOrDefault(g => g.Count() > 1) is { } twice)
            throw new InvalidDataException(
                $"The clip {twice.Key.Id} of {twice.Key.Model} ({twice.Key.Provider}, {twice.Key.Room}) is in more than one input file.");

        var savedRows = saved.SelectMany(r => r.Rows).ToList();
        var clips = all.Where(c => c.Set != Corpus.AscendSet).ToList();
        var rows = savedRows.Where(r => r.Set != Corpus.AscendSet).ToList();
        foreach (var group in all.Where(c => c.Set == Corpus.AscendSet).GroupBy(c => (c.Model, c.Provider, c.Room, c.Language)))
        {
            var scored = ScoreAscendClips(group);
            clips.AddRange(scored);
            var template = savedRows.FirstOrDefault(r => r.Model == group.Key.Model && r.Provider == group.Key.Provider && r.Metric == "load_s" && r.Room == "-")
                           ?? throw new InvalidDataException($"No load row for {group.Key.Model} on {group.Key.Provider}.");
            rows.AddRange(AscendMetrics(group.Key.Language, scored).Select(m => template with
            {
                Streams = 1, Set = Corpus.AscendSet, Language = group.Key.Language, Room = group.Key.Room,
                Metric = m.Metric, Value = Math.Round(m.Value, 4), Unit = m.Unit,
            }));
        }

        return (rows, clips);
    }
}
