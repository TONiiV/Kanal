using System.Text;

namespace Kanal.AsrBenchmark;

internal static class Report
{
    public static string Build(DateTime stamp, string machine, IReadOnlyList<Clip> clips, IReadOnlyList<Row> rows,
        IReadOnlyList<ClipRow> clipRows, IReadOnlyList<string> notes)
    {
        string V(Func<Row, bool> where, string metric, string format = "F2") =>
            rows.FirstOrDefault(r => where(r) && r.Metric == metric) is { } row ? row.Value.ToString(format) : "–";

        var md = new StringBuilder()
            .AppendLine($"# Kanal local ASR benchmark — {stamp:yyyy-MM-dd HH:mm}")
            .AppendLine()
            .AppendLine($"{machine}")
            .AppendLine()
            .AppendLine($"Clips: {string.Join(", ", clips.GroupBy(c => $"{c.Set}/{c.Lang}").Select(g => $"{g.Key} {g.Count()} ({g.Sum(c => c.Seconds):F0} s)"))}.")
            .AppendLine("FLEURS test split (Conneau et al. 2022, `google/fleurs`, CC-BY-4.0). Meeting clips: the 12 sentences of the MT benchmark, synthesised locally with Piper voices through sherpa-onnx.")
            .AppendLine("Every run feeds 20 ms frames through `NemotronAsrProvider`'s session. RTF = decode wall time / audio duration. CER for zh, WER for de/pl, no number normalisation.")
            .AppendLine();

        var engines = rows.Where(r => r.Metric == "load_s" && r.Room == "-").Select(r => (r.Model, r.Provider, r.Threads)).Distinct().ToList();

        md.AppendLine("## Load and memory").AppendLine()
            .AppendLine("| Model | Provider | Threads | Load s | Working set after load MB | Peak working set MB |")
            .AppendLine("|---|---|---:|---:|---:|---:|");
        foreach (var (model, provider, threads) in engines)
        {
            bool W(Row r) => r.Model == model && r.Provider == provider && r.Threads == threads && r.Room == "-";
            md.AppendLine($"| {model} | {provider} | {threads} | {V(W, "load_s")} | {V(W, "working_set_after_load_mb", "F0")} | {V(W, "peak_working_set_mb", "F0")} |");
        }

        md.AppendLine().AppendLine("## Accuracy and offline-feed speed (1 stream)").AppendLine()
            .AppendLine("Forced: a room of the clip's language. Auto: a zh/de/pl room. Language guess: share of finals that `TranscriptLanguage.Guess` labels with the clip's language.")
            .AppendLine()
            .AppendLine("| Model | Provider | Set | Lang | Metric | Forced % | Auto % | Language guess % | Empty (forced/auto) | Codes kept strict (forced/auto) | Codes kept loose (forced/auto) | RTF forced | RTF auto | Decode p50/p95 ms |")
            .AppendLine("|---|---|---|---|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|");
        foreach (var (model, provider, threads) in engines)
        foreach (var set in new[] { "fleurs", "meeting" })
        foreach (var lang in Corpus.Languages)
        {
            bool F(Row r) => Match(r, model, provider, threads, set, lang, "forced");
            bool A(Row r) => Match(r, model, provider, threads, set, lang, "auto");
            if (!rows.Any(F) && !rows.Any(A))
                continue;
            var metric = lang == "zh" ? "cer_pct" : "wer_pct";
            var codes = set == "meeting" ? V(F, "codes_total", "F0") : null;
            md.AppendLine($"| {model} | {provider} | {set} | {lang} | {(lang == "zh" ? "CER" : "WER")} | {V(F, metric, "F1")} | {V(A, metric, "F1")} | {V(A, "lang_guess_pct", "F0")} | " +
                          $"{V(F, "empty_clips", "F0")}/{V(A, "empty_clips", "F0")} | " +
                          (codes is null ? "– | – | " : $"{V(F, "codes_kept_strict", "F0")}/{V(A, "codes_kept_strict", "F0")} of {codes} | {V(F, "codes_kept_loose", "F0")}/{V(A, "codes_kept_loose", "F0")} of {codes} | ") +
                          $"{V(F, "rtf", "F3")} | {V(A, "rtf", "F3")} | {V(F, "decode_p50_ms", "F0")}/{V(F, "decode_p95_ms", "F0")} |");
        }

        var parity = rows.FirstOrDefault(r => r.Metric == "parity_identical");
        if (parity is not null)
            md.AppendLine().AppendLine($"Parity: the product `NemotronAsrProvider` and the benchmark engine gave identical text on {parity.Value:F0} of {V(r => r.Room == parity.Room, "parity_clips", "F0")} meeting clips.");

        md.AppendLine().AppendLine("## Real-time feed (auto room, 20 ms frames at real-time pace, 4 s of silence after each clip)").AppendLine()
            .AppendLine("Decode: one call that ran the model, about one chunk. Final: last final transcript after the last clip sample. First partial: first transcript after the speech onset (first frame within 20 dB of the loudest frame).")
            .AppendLine()
            .AppendLine("| Model | Provider | Threads | Clips | Chunk ms | Decode p50/p95/max ms | Decodes over chunk % | Final after audio end p50/p95 s | First partial after onset p50/p95 s |")
            .AppendLine("|---|---|---:|---:|---:|---:|---:|---:|---:|");
        foreach (var (model, provider, threads) in engines)
        {
            bool R(Row r) => r.Model == model && r.Provider == provider && r.Threads == threads && r.Room == "auto-realtime";
            if (!rows.Any(R))
                continue;
            md.AppendLine($"| {model} | {provider} | {threads} | {V(R, "clips", "F0")} | {V(R, "chunk_ms", "F0")} | {V(R, "decode_p50_ms", "F0")}/{V(R, "decode_p95_ms", "F0")}/{V(R, "decode_max_ms", "F0")} | " +
                          $"{V(R, "decode_over_chunk_pct", "F1")} | {V(R, "final_after_audio_end_p50_s")}/{V(R, "final_after_audio_end_p95_s")} | {V(R, "first_partial_after_onset_p50_s")}/{V(R, "first_partial_after_onset_p95_s")} |");
        }

        md.AppendLine().AppendLine("## Concurrent streams (offline feed, auto room, each stream its own clip order)").AppendLine()
            .AppendLine("| Model | Provider | Threads per stream | Streams | Mean RTF | p95 RTF | Decode p50/p95 ms | Throughput × real time |")
            .AppendLine("|---|---|---:|---:|---:|---:|---:|---:|");
        foreach (var level in rows.Where(r => r.Room == "streams").Select(r => (r.Model, r.Provider, r.Threads, r.Streams)).Distinct())
        {
            bool S(Row r) => r.Model == level.Model && r.Provider == level.Provider && r.Threads == level.Threads && r.Streams == level.Streams && r.Room == "streams";
            md.AppendLine($"| {level.Model} | {level.Provider} | {level.Threads} | {level.Streams} | {V(S, "rtf_mean", "F3")} | {V(S, "rtf_p95", "F3")} | {V(S, "decode_p50_ms", "F0")}/{V(S, "decode_p95_ms", "F0")} | {V(S, "throughput_x_realtime", "F1")} |");
        }

        md.AppendLine().AppendLine("## Thread count (default model, CPU, 1 stream, offline feed, auto room)").AppendLine()
            .AppendLine("| Model | Threads | Load s | RTF | Decode p50/p95 ms |")
            .AppendLine("|---|---:|---:|---:|---:|");
        foreach (var (model, threads) in rows.Where(r => r.Room == "threads").Select(r => (r.Model, r.Threads)).Distinct())
        {
            bool T(Row r) => r.Model == model && r.Threads == threads && r.Room == "threads";
            md.AppendLine($"| {model} | {threads} | {V(T, "load_s")} | {V(T, "rtf", "F3")} | {V(T, "decode_p50_ms", "F0")}/{V(T, "decode_p95_ms", "F0")} |");
        }

        if (notes.Count > 0)
        {
            md.AppendLine().AppendLine("## Notes").AppendLine();
            foreach (var note in notes)
                md.AppendLine($"- {note}");
        }

        md.AppendLine().AppendLine("## Meeting sentences").AppendLine()
            .AppendLine("| Model | Provider | Room | Clip | Reference | Hypothesis | Errors / length |")
            .AppendLine("|---|---|---|---|---|---|---:|");
        foreach (var c in clipRows.Where(c => c.Set == "meeting"))
            md.AppendLine($"| {c.Model} | {c.Provider} | {c.Room} | {c.Id} | {c.Reference} | {c.Hypothesis} | {c.Errors}/{c.Length} |");

        return md.ToString();
    }

    private static bool Match(Row r, string model, string provider, int threads, string set, string lang, string room) =>
        r.Model == model && r.Provider == provider && r.Threads == threads && r.Set == set && r.Language == lang && r.Room == room;
}
