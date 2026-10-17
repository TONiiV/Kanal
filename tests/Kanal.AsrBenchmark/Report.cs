using System.Diagnostics.CodeAnalysis;
using System.Text;

namespace Kanal.AsrBenchmark;

[ExcludeFromCodeCoverage]
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
            .AppendLine("FLEURS test split (Conneau et al. 2022, `google/fleurs`, CC-BY-4.0). Meeting clips: the 12 sentences of the MT benchmark in each language (4 originals, 8 translations), synthesised locally with Piper voices through sherpa-onnx.")
            .AppendLine("Every run feeds 20 ms frames through `NemotronAsrProvider`'s session. RTF = session wall time / audio fed. Audio fed = clip plus the padding of the engine: Nemotron adds 2 s of silence (1 s at the open, 1 s at the finish), the offline engines add none. CER for zh, WER for de/pl/en. Raw scores use no number normalisation. The numbers columns first turn spelled-out cardinals, decimals and percentages into digits in both texts (`Numbers.Normalize`). The no-digit columns score only clips whose reference has no digit. Gladia: the product `GladiaAsrProvider` without translation, fed at real-time pace with 1 s of trailing silence. The product sends Gladia no room languages, so Gladia has only the auto room.")
            .AppendLine();

        var engines = rows.Where(r => r.Metric == "load_s" && r.Room == "-").Select(r => (r.Model, r.Provider, r.Threads)).Distinct().ToList();

        md.AppendLine("## Load and memory").AppendLine()
            .AppendLine("| Model | Provider | Threads | Load s | Working set after load MB | Peak working set MB | Peak minus baseline MB |")
            .AppendLine("|---|---|---:|---:|---:|---:|---:|");
        foreach (var (model, provider, threads) in engines)
        {
            bool W(Row r) => r.Model == model && r.Provider == provider && r.Threads == threads && r.Room == "-";
            md.AppendLine($"| {model} | {provider} | {threads} | {V(W, "load_s")} | {V(W, "working_set_after_load_mb", "F0")} | {V(W, "peak_working_set_mb", "F0")} | {V(W, "peak_delta_mb", "F0")} |");
        }

        md.AppendLine().AppendLine("## Accuracy and offline-feed speed (1 stream)").AppendLine()
            .AppendLine("Forced: a room of the clip's language. Auto: a zh/de/pl/en room. Language guess: share of finals that `TranscriptLanguage.Guess` labels with the clip's language.")
            .AppendLine()
            .AppendLine("| Model | Provider | Set | Lang | Metric | Forced % | Auto % | Numbers forced/auto % | No-digit clips n | No-digit forced/auto % | Language guess % | Empty (forced/auto) | Codes kept strict (forced/auto) | Codes kept loose (forced/auto) | RTF forced | RTF auto | Decode p50/p95 ms |")
            .AppendLine("|---|---|---|---|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|");
        foreach (var (model, provider, threads) in engines)
        foreach (var set in clips.Select(c => c.Set).Distinct())
        foreach (var lang in Corpus.Languages)
        {
            bool F(Row r) => Match(r, model, provider, threads, set, lang, "forced");
            bool A(Row r) => Match(r, model, provider, threads, set, lang, "auto");
            if (!rows.Any(F) && !rows.Any(A))
                continue;
            var metric = lang == "zh" ? "cer_pct" : "wer_pct";
            var noDigit = lang == "zh" ? "cer_no_digit_pct" : "wer_no_digit_pct";
            var numbers = lang == "zh" ? "cer_numbers_pct" : "wer_numbers_pct";
            var codes = set == "meeting" ? V(F, "codes_total", "F0") : null;
            md.AppendLine($"| {model} | {provider} | {set} | {lang} | {(lang == "zh" ? "CER" : "WER")} | {V(F, metric, "F1")} | {V(A, metric, "F1")} | {V(F, numbers, "F1")}/{V(A, numbers, "F1")} | {V(A, "clips_no_digit", "F0")} | {V(F, noDigit, "F1")}/{V(A, noDigit, "F1")} | {V(A, "lang_guess_pct", "F0")} | " +
                          $"{V(F, "empty_clips", "F0")}/{V(A, "empty_clips", "F0")} | " +
                          (codes is null ? "– | – | " : $"{V(F, "codes_kept_strict", "F0")}/{V(A, "codes_kept_strict", "F0")} of {codes} | {V(F, "codes_kept_loose", "F0")}/{V(A, "codes_kept_loose", "F0")} of {codes} | ") +
                          $"{V(F, "rtf", "F3")} | {V(A, "rtf", "F3")} | {V(F, "decode_p50_ms", "F0")}/{V(F, "decode_p95_ms", "F0")} |");
        }

        var parity = rows.FirstOrDefault(r => r.Metric == "parity_identical");
        if (parity is not null)
            md.AppendLine().AppendLine($"Parity: the product `NemotronAsrProvider` and the benchmark engine gave identical text on {parity.Value:F0} of {V(r => r.Room == parity.Room, "parity_clips", "F0")} meeting clips.");

        md.AppendLine().AppendLine("## Real-time feed (auto room, 20 ms frames at real-time pace, 4 s of silence after each clip)").AppendLine()
            .AppendLine("Percentiles use the nearest rank over the clips in the Clips column. With fewer than 20 clips, p95 is the maximum or close to it. Decode: one call that ran the model, about one chunk. Final: last final transcript after the speech end (end of the last frame within 20 dB of the loudest frame). First partial: first transcript after the speech onset (first frame within 20 dB of the loudest frame).")
            .AppendLine()
            .AppendLine("| Model | Provider | Threads | Clips | Chunk ms | Decode p50/p95/max ms | Decodes over chunk % | Final after speech end p50/p95 s | First partial after onset p50/p95 s |")
            .AppendLine("|---|---|---:|---:|---:|---:|---:|---:|---:|");
        foreach (var (model, provider, threads) in engines)
        {
            bool R(Row r) => r.Model == model && r.Provider == provider && r.Threads == threads && r.Room == "auto-realtime";
            if (!rows.Any(R))
                continue;
            md.AppendLine($"| {model} | {provider} | {threads} | {V(R, "clips", "F0")} | {V(R, "chunk_ms", "F0")} | {V(R, "decode_p50_ms", "F0")}/{V(R, "decode_p95_ms", "F0")}/{V(R, "decode_max_ms", "F0")} | " +
                          $"{V(R, "decode_over_chunk_pct", "F1")} | {V(R, "final_after_speech_end_p50_s")}/{V(R, "final_after_speech_end_p95_s")} | {V(R, "first_partial_after_onset_p50_s")}/{V(R, "first_partial_after_onset_p95_s")} |");
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

        md.AppendLine().AppendLine(Research);

        md.AppendLine().AppendLine("## Meeting sentences").AppendLine()
            .AppendLine("| Model | Provider | Room | Clip | Reference | Hypothesis | Errors / length | Numbers errors / length |")
            .AppendLine("|---|---|---|---|---|---|---:|---:|");
        foreach (var c in clipRows.Where(c => c.Set == "meeting"))
            md.AppendLine($"| {c.Model} | {c.Provider} | {c.Room} | {c.Id} | {c.Reference} | {c.Hypothesis} | {c.Errors}/{c.Length} | {c.NumbersErrors}/{c.NumbersLength} |");

        return md.ToString();
    }

    // Researched on 2026-10-17 from the sherpa-onnx asr-models release, the HF model cards and the licence texts.
    private const string Research = """
        ## Candidate models that sherpa-onnx 1.13.8 can run

        | Model | Params | int8 download | zh | de | pl | Mode | Licence | Repo | Measured |
        |---|---:|---:|:-:|:-:|:-:|---|---|---|:-:|
        | Nemotron 3.5 ASR Streaming 560 ms (default) | 0.6B | 682 MB | ✓ | ✓ | ✓ | streaming | OpenMDW-1.1, no regional clause | csukuangfj2/sherpa-onnx-nemotron-3.5-asr-streaming-0.6b-560ms-int8-2026-06-11 | ✓ |
        | Nemotron 3.5 ASR Streaming 160 ms | 0.6B | 682 MB | ✓ | ✓ | ✓ | streaming | OpenMDW-1.1 | csukuangfj2/…-160ms-int8-2026-06-11 (also 80, 320, 1120 ms) | ✓ |
        | Whisper large-v3-turbo | 809M | 1036 MB | ✓ | ✓ | ✓ | offline, 30 s window | MIT | csukuangfj/sherpa-onnx-whisper-turbo | ✓ |
        | Qwen3-ASR 0.6B | 0.6B | 985 MB | ✓ | ✓ | ✓ | offline in sherpa-onnx (upstream also streams) | Apache-2.0 | csukuangfj2/sherpa-onnx-qwen3-asr-0.6B-int8-2026-03-25 | ✓ |
        | Whisper small | 244M | 375 MB | ✓ | ✓ | ✓ | offline, 30 s window | Apache-2.0 (HF card) | csukuangfj/sherpa-onnx-whisper-small | – |
        | Whisper tiny / base | 39M / 74M | 116 / 208 MB archive | ✓ | ✓ | ✓ | offline | Apache-2.0 (HF card) | csukuangfj/sherpa-onnx-whisper-tiny, -base | – |
        | Cohere Transcribe 03-2026 | 2.1B | 2888 MB | ✓ | ✓ | ✓ | offline | Apache-2.0, HF repo gated | csukuangfj2/sherpa-onnx-cohere-transcribe-14-lang-int8-2026-04-01 | – |
        | Omnilingual ASR CTC 300M v2 | 300M | 292 MB archive | ✓ | ✓ | ✓ | offline CTC, 1600 languages | Apache-2.0 | GitHub release asr-models (facebook/omniASR-CTC-300M) | – |
        | Parakeet TDT 0.6B v3 | 0.6B | 670 MB | – | ✓ | ✓ | offline | CC-BY-4.0 | csukuangfj/sherpa-onnx-nemo-parakeet-tdt-0.6b-v3-int8 | – |
        | NeMo FastConformer transducer be/de/en/es/fr/hr/it/pl/ru/uk | ~115M | 107 MB archive | – | ✓ | ✓ | offline | not checked | GitHub release asr-models | – |
        | Canary 180M flash | 180M | 154 MB archive | – | ✓ | – | offline | CC-BY-4.0 | GitHub release asr-models | – |
        | SenseVoice small, Fun-ASR-Nano 2512, Dolphin, Moonshine zh | 0.2–0.8B | 0.1–0.9 GB | ✓ | – | – | offline | mixed | – | – |

        The measured set is capped at 3 extra models and 3 GB. Offline models use Silero VAD segments (500 ms minimum silence, 20 s maximum segment).
        """;

    private static bool Match(Row r, string model, string provider, int threads, string set, string lang, string room) =>
        r.Model == model && r.Provider == provider && r.Threads == threads && r.Set == set && r.Language == lang && r.Room == room;
}
