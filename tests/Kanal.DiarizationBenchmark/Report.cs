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
        var md = new StringBuilder()
            .AppendLine($"# Kanal diarization benchmark — {run.Stamp:yyyy-MM-dd HH:mm}")
            .AppendLine()
            .AppendLine(run.Machine)
            .AppendLine()
            .AppendLine($"{meetings.Count} meetings, {run.Costs.Where(c => c.Model == models[0]).GroupBy(c => c.Meeting).Sum(g => g.First().AudioSeconds) / 60:F0} min of audio, {meetings.Sum(m => m.Total)} utterances ({meetings.Sum(m => m.ShortTotal)} under 1 s).")
            .AppendLine($"Room channel: {run.Room}.")
            .AppendLine($"DER: 10 ms frames, Hungarian speaker mapping, overlap scored as is (a frame with two reference speakers counts twice). Library default clustering threshold: {run.DefaultThreshold:0.0}.")
            .AppendLine("Utterance accuracy: SpeakerReidentification.Assign gives each reference utterance a tag; one Hungarian mapping per meeting maps tags to speakers; an untagged utterance is wrong.")
            .AppendLine();

        var scripted = r.Where(x => x.Meeting.StartsWith("negotiation", StringComparison.Ordinal)).ToList();
        var synthetic = r.Except(scripted).ToList();
        if (scripted.Count > 0)
        {
            Systems(md, "Scripted 6-speaker meeting (clean and room)", scripted, models, run.DefaultThreshold);
            Live(md, "Live layer, scripted meeting", scripted, models);
        }

        var everyModel = synthetic.GroupBy(x => x.Meeting)
            .Where(g => g.Select(x => (x.Model, x.Threshold)).Distinct().Count() == g.Where(x => x.Model == models[0]).Select(x => (x.Model, x.Threshold)).Distinct().Count() * models.Count)
            .Select(g => g.Key).ToHashSet();
        var full = synthetic.Where(x => everyModel.Contains(x.Meeting)).ToList();
        Systems(md, $"Synthetic meetings with the full sweep for every embedding ({everyModel.Count})", full, models, run.DefaultThreshold);
        Systems(md, $"All synthetic meetings ({synthetic.Select(x => x.Meeting).Distinct().Count()})", synthetic, models, run.DefaultThreshold);
        Sweep(md, "Threshold sweep without a count, all synthetic meetings", synthetic, models);

        foreach (var model in models)
        foreach (var system in Labels(synthetic, model, run.DefaultThreshold))
        {
            var set = synthetic.Where(x => x.Model == model && system.Pick(x)).ToList();
            md.AppendLine().AppendLine($"## {model}, {system.Label}: DER % / utterance accuracy % / speakers found").AppendLine()
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

        md.AppendLine();
        Live(md, "Live layer, all synthetic meetings", synthetic, models);

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

        if (run.Details is { Count: > 0 })
        {
            md.AppendLine().AppendLine("## Per-speaker attribution, scripted meeting").AppendLine()
                .AppendLine("Rows: true speakers. Columns: tags, with the speaker that the optimal mapping gives each tag. Cells: utterances.").AppendLine();
            foreach (var detail in run.Details)
                md.AppendLine(detail);
        }

        return md.ToString();
    }

    private static List<(string Label, Func<Result, bool> Pick)> Labels(List<Result> r, string model, double defaultThreshold)
    {
        var mine = r.Where(x => x.Model == model).ToList();
        var thresholds = mine.Where(x => x.System == "auto").Select(x => x.Threshold).Distinct().ToList();
        var best = thresholds.MinBy(t => Mean(mine.Where(x => x.System == "auto" && x.Threshold == t), x => x.Der));
        var labels = new List<(string, Func<Result, bool>)> { ("count given", x => x.System == "count") };
        foreach (var t in new[] { defaultThreshold, 0.8, best }.Select(t => Math.Round(t, 2)).Distinct().Where(t => thresholds.Any(u => Near(u, t))))
        {
            var label = $"threshold {t:0.0}" + (Near(t, defaultThreshold) ? " (default)" : "") + (thresholds.Count > 2 && Near(t, best) ? " (best mean DER)" : "");
            labels.Add((label, x => x.System == "auto" && Near(x.Threshold, t)));
        }

        return labels;
    }

    private static void Systems(StringBuilder md, string title, List<Result> r, List<string> models, double defaultThreshold)
    {
        md.AppendLine($"## {title}").AppendLine()
            .AppendLine("| Embedding | System | Meetings | DER % (0.25 s collar) | Missed / FA / Confusion % | DER % no collar | Utterance accuracy % | ≥ 1 s % | < 1 s % | Count exact % | Mean found − true |")
            .AppendLine("|---|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|");
        foreach (var model in models)
        foreach (var (label, pick) in Labels(r, model, defaultThreshold))
            Row(md, model, label, r.Where(x => x.Model == model && pick(x)).ToList());
        md.AppendLine();
    }

    private static void Sweep(StringBuilder md, string title, List<Result> r, List<string> models)
    {
        md.AppendLine($"## {title}").AppendLine()
            .AppendLine("| Embedding | Threshold | Meetings | DER % all | zh | de | pl | mix | Utterance accuracy % | Count exact % | Mean found − true |")
            .AppendLine("|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|");
        foreach (var model in models)
        foreach (var t in r.Where(x => x.Model == model && x.System == "auto").Select(x => x.Threshold).Distinct().Order())
        {
            var set = r.Where(x => x.Model == model && x.System == "auto" && x.Threshold == t).ToList();
            md.AppendLine($"| {model} | {t:0.0} | {set.Count} | {P(Mean(set, x => x.Der))} | {string.Join(" | ", Corpus.Languages.Select(l => P(Mean(set.Where(x => x.Lang == l), x => x.Der))))} | {P(Acc(set))} | {P(Exact(set))} | {Mean(set, x => x.Estimated - x.Speakers):+0.0;-0.0;0.0} |");
        }

        md.AppendLine();
    }

    private static void Live(StringBuilder md, string title, List<Result> r, List<string> models)
    {
        md.AppendLine($"## {title} (LiveSpeakerLabels over the reference utterances)").AppendLine()
            .AppendLine("| Embedding | Threshold | Meetings | Utterance accuracy % | ≥ 1 s % | < 1 s % | zh | de | pl | mix | Speakers created (mean) | True (mean) |")
            .AppendLine("|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|");
        foreach (var model in models)
        foreach (var t in r.Where(x => x.Model == model && x.System == "live").Select(x => x.Threshold).Distinct().Order())
        {
            var set = r.Where(x => x.Model == model && x.System == "live" && x.Threshold == t).ToList();
            md.AppendLine($"| {model} | {t:0.0} | {set.Count} | {P(Acc(set))} | {P(Long(set))} | {P(Short(set))} | {string.Join(" | ", Corpus.Languages.Select(l => P(Acc(set.Where(x => x.Lang == l)))))} | {Mean(set, x => x.Estimated):F1} | {Mean(set, x => x.Speakers):F1} |");
        }

        md.AppendLine();
    }

    private static void Row(StringBuilder md, string model, string label, List<Result> set) =>
        md.AppendLine($"| {model} | {label} | {set.Count} | {P(Mean(set, x => x.Der))} | {P(Mean(set, x => x.Missed))} / {P(Mean(set, x => x.FalseAlarm))} / {P(Mean(set, x => x.Confusion))} | {P(Mean(set, x => x.DerNoCollar))} | {P(Acc(set))} | {P(Long(set))} | {P(Short(set))} | {P(Exact(set))} | {Mean(set, x => x.Estimated - x.Speakers):+0.0;-0.0;0.0} |");

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
