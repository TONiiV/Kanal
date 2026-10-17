using System.Diagnostics.CodeAnalysis;
using System.Text;

namespace Kanal.DiarizationBenchmark;

[ExcludeFromCodeCoverage]
internal static class Report
{
    private static readonly string[] Variants = ["clean", "overlap", "room", "overlap-room"];

    public static string Build(Run run)
    {
        var r = run.Results;
        var models = r.Select(x => x.Model).Distinct().ToList();
        var meetings = r.GroupBy(x => x.Meeting).Select(g => g.First()).ToList();
        var thresholds = r.Where(x => x.System == "auto").Select(x => x.Threshold).Distinct().Order().ToList();
        var liveThresholds = r.Where(x => x.System == "live").Select(x => x.Threshold).Distinct().Order().ToList();
        var md = new StringBuilder()
            .AppendLine($"# Kanal diarization benchmark — {run.Stamp:yyyy-MM-dd HH:mm}")
            .AppendLine()
            .AppendLine(run.Machine)
            .AppendLine()
            .AppendLine($"{meetings.Count} synthetic meetings, {run.Costs.Where(c => c.Model == models[0]).GroupBy(c => c.Meeting).Sum(g => g.First().AudioSeconds) / 60:F0} min of audio, {meetings.Sum(m => m.Total)} utterances ({meetings.Sum(m => m.ShortTotal)} under 1 s).")
            .AppendLine($"Room channel: {run.Room}.")
            .AppendLine($"DER: 10 ms frames, Hungarian speaker mapping, overlap scored as is (a frame with two reference speakers counts twice). Library default clustering threshold: {run.DefaultThreshold:0.0}.")
            .AppendLine("Utterance accuracy: SpeakerReidentification.Assign gives each reference utterance a tag; one Hungarian mapping per meeting maps tags to speakers; an untagged utterance is wrong.")
            .AppendLine();

        double Best(string model) => thresholds.MinBy(t => Mean(r.Where(x => x.Model == model && x.System == "auto" && x.Threshold == t), x => x.Der));
        var bestOf = models.ToDictionary(m => m, Best);

        md.AppendLine("## Systems, all meetings").AppendLine()
            .AppendLine("| Embedding | System | DER % (0.25 s collar) | Missed / FA / Confusion % | DER % no collar | Utterance accuracy % | ≥ 1 s % | < 1 s % | Count exact % | Mean found − true |")
            .AppendLine("|---|---|---:|---:|---:|---:|---:|---:|---:|---:|");
        foreach (var model in models)
        {
            Row(md, model, "count given", r.Where(x => x.Model == model && x.System == "count").ToList());
            Row(md, model, $"threshold {run.DefaultThreshold:0.0} (default)", r.Where(x => x.Model == model && x.System == "auto" && Near(x.Threshold, run.DefaultThreshold)).ToList());
            Row(md, model, $"threshold {bestOf[model]:0.0} (best mean DER)", r.Where(x => x.Model == model && x.System == "auto" && x.Threshold == bestOf[model]).ToList());
        }

        md.AppendLine().AppendLine("## Threshold sweep without a count").AppendLine()
            .AppendLine("| Embedding | Threshold | DER % all | zh | de | pl | mix | Utterance accuracy % | Count exact % | Mean found − true |")
            .AppendLine("|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|");
        foreach (var model in models)
        foreach (var t in thresholds)
        {
            var set = r.Where(x => x.Model == model && x.System == "auto" && x.Threshold == t).ToList();
            md.AppendLine($"| {model} | {t:0.0} | {P(Mean(set, x => x.Der))} | {string.Join(" | ", Corpus.Languages.Select(l => P(Mean(set.Where(x => x.Lang == l), x => x.Der))))} | {P(Acc(set))} | {P(Exact(set))} | {Mean(set, x => x.Estimated - x.Speakers):+0.0;-0.0;0.0} |");
        }

        foreach (var model in models)
        {
            foreach (var (label, pick) in new (string, Func<Result, bool>)[]
                     {
                         ("count given", x => x.System == "count"),
                         ($"threshold {run.DefaultThreshold:0.0}", x => x.System == "auto" && Near(x.Threshold, run.DefaultThreshold)),
                         ($"threshold {bestOf[model]:0.0}", x => x.System == "auto" && x.Threshold == bestOf[model]),
                     })
            {
                var set = r.Where(x => x.Model == model && pick(x)).ToList();
                md.AppendLine().AppendLine($"## {model}, {label}: DER % / utterance accuracy % / speakers found").AppendLine()
                    .AppendLine("| Language | Speakers | " + string.Join(" | ", Variants) + " |")
                    .AppendLine("|---|---:|" + string.Concat(Variants.Select(_ => "---:|")));
                foreach (var lang in Corpus.Languages)
                foreach (var n in Corpus.SpeakerCounts)
                {
                    var cells = Variants.Select(v => set.FirstOrDefault(x => x.Lang == lang && x.Speakers == n && Variant(x) == v) is { } x
                        ? $"{P(x.Der)} / {P(x.Accuracy)} / {x.Estimated}"
                        : "–");
                    if (set.Any(x => x.Lang == lang && x.Speakers == n))
                        md.AppendLine($"| {lang} | {n} | {string.Join(" | ", cells)} |");
                }
            }
        }

        md.AppendLine().AppendLine("## Live layer (LiveSpeakerLabels over the reference utterances)").AppendLine()
            .AppendLine("| Embedding | Threshold | Utterance accuracy % | ≥ 1 s % | < 1 s % | zh | de | pl | mix | Speakers created (mean) | True (mean) |")
            .AppendLine("|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|");
        foreach (var model in models)
        foreach (var t in liveThresholds)
        {
            var set = r.Where(x => x.Model == model && x.System == "live" && x.Threshold == t).ToList();
            md.AppendLine($"| {model} | {t:0.0} | {P(Acc(set))} | {P(Long(set))} | {P(Short(set))} | {string.Join(" | ", Corpus.Languages.Select(l => P(Acc(set.Where(x => x.Lang == l)))))} | {Mean(set, x => x.Estimated):F1} | {Mean(set, x => x.Speakers):F1} |");
        }

        md.AppendLine().AppendLine("## Cost (post-meeting layer, one diarization per run)").AppendLine()
            .AppendLine("| Embedding | Runs | Load s (mean) | RTF mean | RTF max | Peak working set MB | Peak minus baseline MB |")
            .AppendLine("|---|---:|---:|---:|---:|---:|---:|");
        foreach (var model in models)
        {
            var c = run.Costs.Where(x => x.Model == model).ToList();
            md.AppendLine($"| {model} | {c.Count} | {c.Average(x => x.LoadSeconds):F2} | {c.Average(x => x.Seconds / x.AudioSeconds):F3} | {c.Max(x => x.Seconds / x.AudioSeconds):F3} | {c.Max(x => x.PeakMb):F0} | {c.Max(x => x.PeakDeltaMb):F0} |");
        }

        if (run.Notes.Count > 0)
        {
            md.AppendLine().AppendLine("## Notes").AppendLine();
            foreach (var note in run.Notes)
                md.AppendLine($"- {note}");
        }

        return md.ToString();
    }

    private static void Row(StringBuilder md, string model, string label, List<Result> set) =>
        md.AppendLine($"| {model} | {label} | {P(Mean(set, x => x.Der))} | {P(Mean(set, x => x.Missed))} / {P(Mean(set, x => x.FalseAlarm))} / {P(Mean(set, x => x.Confusion))} | {P(Mean(set, x => x.DerNoCollar))} | {P(Acc(set))} | {P(Long(set))} | {P(Short(set))} | {P(Exact(set))} | {Mean(set, x => x.Estimated - x.Speakers):+0.0;-0.0;0.0} |");

    public static string Variant(Result x) => (x.Overlap, x.Room) switch
    {
        (false, false) => "clean",
        (true, false) => "overlap",
        (false, true) => "room",
        _ => "overlap-room",
    };

    private static bool Near(double a, double b) => Math.Abs(a - b) < 1e-4;

    private static double Mean(IEnumerable<Result> set, Func<Result, double> value) =>
        set.Select(value).DefaultIfEmpty(double.NaN).Average();

    private static double Acc(IEnumerable<Result> set) => Ratio(set.Sum(x => x.Correct), set.Sum(x => x.Total));

    private static double Long(IEnumerable<Result> set) => Ratio(set.Sum(x => x.LongCorrect), set.Sum(x => x.LongTotal));

    private static double Short(IEnumerable<Result> set) => Ratio(set.Sum(x => x.ShortCorrect), set.Sum(x => x.ShortTotal));

    private static double Exact(IEnumerable<Result> set) => set.Select(x => x.Estimated == x.Speakers ? 1.0 : 0).DefaultIfEmpty(double.NaN).Average();

    private static double Ratio(int a, int b) => b == 0 ? double.NaN : a / (double)b;

    private static string P(double v) => double.IsNaN(v) ? "–" : (v * 100).ToString("F1");
}
