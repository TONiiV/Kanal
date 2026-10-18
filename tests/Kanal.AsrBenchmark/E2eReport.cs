using System.Diagnostics.CodeAnalysis;
using System.Text;

namespace Kanal.AsrBenchmark;

[ExcludeFromCodeCoverage]
internal static class E2eReport
{
    public static readonly IReadOnlyDictionary<string, string> RouteNames = new Dictionary<string, string>
    {
        ["A"] = "Gladia, transcription and translation in one session",
        ["B"] = "Gladia transcription, then Hy-MT2 1.8B",
        ["C"] = "Nemotron 560 ms, then Hy-MT2 1.8B",
        ["D"] = "Qwen3-ASR 0.6B, then Hy-MT2 1.8B",
    };

    public static string Build(DateTime stamp, string machine, IReadOnlyList<E2eRun> runs, IReadOnlyList<string> notes)
    {
        var routes = runs.Select(r => r.Route).Distinct().Order().ToList();
        var summary = E2e.Summarise(runs);
        string Name(string route) => RouteNames.GetValueOrDefault(route, route);
        string Seconds(double value) => value.ToString("F2");
        string Latency(IEnumerable<E2eRun> set, bool p95)
        {
            var latency = E2e.Latency(set);
            return latency.N == 0 ? "–" : Seconds(p95 ? latency.P95 : latency.P50);
        }

        string Asr(IEnumerable<E2eRun> set) => set.Any() ? E2e.AsrErrorPct(set).ToString("F1") : "–";
        string Chrf(string route, string source, string target) =>
            summary.FirstOrDefault(r => r.Route == route && r.Source == source && r.Target == target) is { } row ? row.ChrF.ToString("F1") : "–";
        string Codes(string route, string source, string target, bool loose) =>
            summary.FirstOrDefault(r => r.Route == route && r.Source == source && r.Target == target) is { } row
                ? $"{(loose ? row.Loose : row.Strict)}/{row.Codes}"
                : "–";

        var md = new StringBuilder()
            .AppendLine($"# Kanal end-to-end speech translation benchmark — {stamp:yyyy-MM-dd HH:mm}")
            .AppendLine()
            .AppendLine(machine)
            .AppendLine()
            .AppendLine("Audio: the `meeting-11labs` set. It has 12 meeting sentences in zh, de, pl and en, each in 3 ElevenLabs voices. Every run uses the auto room (zh, de, pl, en). The translation targets of a clip are the other three room languages. The reference translation of sentence i into language T is the T version of sentence i. The 12 sentences are parallel, and they are the references of the MT benchmark.")
            .AppendLine()
            .AppendLine("Audio is fed in 20 ms frames at real-time pace. Latency is the time from the end of speech to the last translated text of the clip. Local routes add the ASR final time and the MT time. MT runs on the CPU.")
            .AppendLine()
            .AppendLine("chrF is the mean over clips and targets of the sentence chrF (character 1..6-grams, beta 2). A missing translation scores 0. Codes: part numbers of the reference translation that the output keeps, strict (verbatim) and loose (case, space, hyphen, dot and slash ignored). ASR error: CER for zh, WER for de, pl and en, numbers normalised. The figure for several languages is the mean of the language rates.")
            .AppendLine();

        md.AppendLine("## Routes").AppendLine()
            .AppendLine("| Route | Clips | chrF | Codes strict | Codes loose | Latency p50 s | Latency p95 s | ASR error % |")
            .AppendLine("|---|---:|---:|---:|---:|---:|---:|---:|");
        foreach (var route in routes)
        {
            var set = runs.Where(r => r.Route == route).ToList();
            md.AppendLine($"| {route} | {set.Count} | {Chrf(route, E2e.All, E2e.All)} | {Codes(route, E2e.All, E2e.All, false)} | {Codes(route, E2e.All, E2e.All, true)} | {Latency(set, false)} | {Latency(set, true)} | {Asr(set)} |");
        }

        md.AppendLine().AppendLine("Route names:").AppendLine();
        foreach (var route in routes)
            md.AppendLine($"- {route}: {Name(route)}.");

        md.AppendLine().AppendLine("## By source language").AppendLine()
            .AppendLine("| Route | Source | Clips | chrF | Codes strict | Codes loose | Latency p50 s | Latency p95 s | ASR error % |")
            .AppendLine("|---|---|---:|---:|---:|---:|---:|---:|---:|");
        foreach (var route in routes)
        foreach (var source in Corpus.Languages)
        {
            var set = runs.Where(r => r.Route == route && r.Clip.Lang == source).ToList();
            if (set.Count > 0)
                md.AppendLine($"| {route} | {source} | {set.Count} | {Chrf(route, source, E2e.All)} | {Codes(route, source, E2e.All, false)} | {Codes(route, source, E2e.All, true)} | {Latency(set, false)} | {Latency(set, true)} | {Asr(set)} |");
        }

        md.AppendLine().AppendLine("## By language pair").AppendLine()
            .AppendLine("| Route | Source | Target | Pairs | chrF | Codes strict | Codes loose |")
            .AppendLine("|---|---|---|---:|---:|---:|---:|");
        foreach (var row in summary.Where(r => r.Source != E2e.All && r.Target != E2e.All))
            md.AppendLine($"| {row.Route} | {row.Source} | {row.Target} | {row.Pairs} | {row.ChrF:F1} | {row.Strict}/{row.Codes} | {row.Loose}/{row.Codes} |");

        md.AppendLine().AppendLine("## Latency parts").AppendLine()
            .AppendLine("ASR is the time from the end of speech to the final transcript. Translation is the rest. Route A has one session, so it has no ASR part.")
            .AppendLine()
            .AppendLine("| Route | ASR final p50 s | ASR final p95 s | Translation p50 s | Translation p95 s |")
            .AppendLine("|---|---:|---:|---:|---:|");
        foreach (var route in routes)
        {
            var set = runs.Where(r => r.Route == route && r.LatencySeconds is not null && r.AsrSeconds is not null).ToList();
            var asr = set.Select(r => r.AsrSeconds!.Value).ToList();
            var rest = set.Select(r => r.LatencySeconds!.Value - r.AsrSeconds!.Value).ToList();
            md.AppendLine(set.Count == 0
                ? $"| {route} | – | – | – | – |"
                : $"| {route} | {Seconds(Scoring.Percentile(asr, 0.5))} | {Seconds(Scoring.Percentile(asr, 0.95))} | {Seconds(Scoring.Percentile(rest, 0.5))} | {Seconds(Scoring.Percentile(rest, 0.95))} |");
        }

        if (notes.Count > 0)
        {
            md.AppendLine().AppendLine("## Notes").AppendLine();
            foreach (var note in notes)
                md.AppendLine($"- {note}");
        }

        md.AppendLine().AppendLine("## Clips").AppendLine()
            .AppendLine("| Route | Clip | Transcript | Source language | Target | Translation | chrF | Latency s |")
            .AppendLine("|---|---|---|---|---|---|---:|---:|");
        foreach (var run in runs.OrderBy(r => r.Route).ThenBy(r => r.Clip.Id, StringComparer.Ordinal))
        foreach (var pair in E2e.Pairs(run))
            md.AppendLine($"| {run.Route} | {run.Clip.Id} | {Cell(run.Transcript)} | {string.Join(",", run.SrcLangs)} | {pair.Target} | {Cell(run.Translations.GetValueOrDefault(pair.Target, ""))} | {pair.ChrF:F1} | {(run.LatencySeconds is { } s ? Seconds(s) : "–")} |");

        return md.ToString();
    }

    private static string Cell(string text) => text.ReplaceLineEndings(" ").Replace("|", "\\|");
}
