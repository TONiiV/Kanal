using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using System.Text.Encodings.Web;
using System.Text.Json;
using Kanal.Core.Models;
using Kanal.Core.Providers;
using Kanal.Core.Text;
using Kanal.Providers.LocalAsr;
using Kanal.Providers.LocalMt;

namespace Kanal.AsrBenchmark;

[ExcludeFromCodeCoverage]
internal static class E2eRunner
{
    private static readonly string[] Room = [.. Corpus.Languages];

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private sealed record Piece(string Src, IReadOnlyDictionary<string, string> Map, double DoneAt, string? Error);

    private sealed record RunDto(string Route, string ClipId, string Transcript, string[] SrcLangs,
        Dictionary<string, string> Translations, double? LatencySeconds, double? AsrSeconds);

    private sealed record RouteFile(string Machine, List<string> Notes, List<RunDto> Runs);

    public static async Task<int> RunAsync(string data, string modelsDir, string outDir, IReadOnlyList<string> routes, int perGroup, string? note)
    {
        var clips = Corpus.Load(data, ["meeting-11labs"]).GroupBy(c => c.Lang).SelectMany(g => g.Take(perGroup)).ToList();
        if (clips.Count == 0)
        {
            Console.Error.WriteLine($"No meeting-11labs clips in {data}. Run: fetch --sets meeting-11labs");
            return 1;
        }

        Directory.CreateDirectory(outDir);
        var machine = $"{Feeder.CpuName()} · {Environment.ProcessorCount} logical CPUs · {RuntimeInformation.OSDescription} ({RuntimeInformation.ProcessArchitecture})";
        Console.WriteLine($"{machine}\n{clips.Count} clips, {clips.Sum(c => c.Seconds) / 60:F1} min of audio");

        foreach (var route in routes)
        {
            var notes = new List<string>();
            if (note is not null)
                notes.Add(note);
            notes.Add(await LoadNoteAsync());
            var runs = new List<E2eRun>();
            switch (route)
            {
                case "gladia":
                    using (var mt = await LoadMtAsync(modelsDir, notes))
                        runs = await GladiaAsync(mt, clips, notes);
                    break;
                case "nemotron":
                    using (var asr = new NemotronAsrProvider(AsrModelCatalog.Models[0], new ModelDownloadManager(modelsDir)))
                    using (var mt = await LoadMtAsync(modelsDir, notes))
                    {
                        await asr.WarmUpAsync(CancellationToken.None);
                        runs = await LocalAsync("C", _ => asr, clips, mt);
                    }

                    break;
                case "qwen3":
                    var model = Engine.Downloaded(data).First(m => m.Id == "qwen3-asr-0.6b-int8");
                    using (var engine = Engine.Load(model, "cpu", Engine.DefaultThreads, data))
                    using (var mt = await LoadMtAsync(modelsDir, notes))
                        runs = await LocalAsync("D", engine.Provider, clips, mt);
                    break;
                default:
                    Console.Error.WriteLine($"Unknown route {route}. Known routes: gladia, nemotron, qwen3.");
                    return 2;
            }

            var file = new RouteFile(machine, notes, [.. runs.Select(r => new RunDto(r.Route, r.Clip.Id, r.Transcript,
                [.. r.SrcLangs], r.Translations.ToDictionary(kv => kv.Key, kv => kv.Value), r.LatencySeconds, r.AsrSeconds))]);
            await File.WriteAllTextAsync(Path.Combine(outDir, $"e2e-runs-{route}.json"), JsonSerializer.Serialize(file, Json));
        }

        var all = new List<E2eRun>();
        var allNotes = new List<string>();
        foreach (var path in Directory.GetFiles(outDir, "e2e-runs-*.json").Order())
        {
            var file = JsonSerializer.Deserialize<RouteFile>(File.ReadAllText(path), Json)!;
            allNotes.AddRange(file.Notes.Select(n => $"{Path.GetFileNameWithoutExtension(path)["e2e-runs-".Length..]}: {n}"));
            all.AddRange(file.Runs.Select(r => new E2eRun(r.Route,
                new Clip("meeting-11labs", r.ClipId[..2], r.ClipId, E2e.Reference(r.ClipId, r.ClipId[..2]), []),
                r.Transcript, r.SrcLangs, r.Translations, r.LatencySeconds, r.AsrSeconds)));
        }

        var stamp = DateTime.Now;
        var basePath = Path.Combine(outDir, $"kanal-e2e-{stamp:yyyyMMdd-HHmm}");
        await File.WriteAllTextAsync(basePath + ".md", E2eReport.Build(stamp, machine, all, allNotes));
        await File.WriteAllTextAsync(basePath + ".json", JsonSerializer.Serialize(new
        {
            generated = stamp,
            machine,
            notes = allNotes,
            summary = E2e.Summarise(all),
            runs = all.Select(r => new
            {
                r.Route,
                clip = r.Clip.Id,
                r.Transcript,
                r.SrcLangs,
                r.Translations,
                r.LatencySeconds,
                r.AsrSeconds,
                pairs = E2e.Pairs(r),
            }),
        }, Json));
        Console.WriteLine($"Report: {basePath}.md");
        return 0;
    }

    private static async Task<LlamaSharpMtProvider> LoadMtAsync(string modelsDir, List<string> notes)
    {
        var info = LocalModelCatalog.Models[0];
        var path = new ModelDownloadManager(modelsDir).GetPath(info);
        if (!File.Exists(path))
            throw new FileNotFoundException($"{info.DisplayName} is missing. Download it in Kanal first.", path);
        var mt = new LlamaSharpMtProvider(new LlamaSharpTextGenerator(path, info.AssistantPrefill));
        var load = Stopwatch.StartNew();
        await mt.WarmUpAsync(CancellationToken.None);
        var loaded = load.Elapsed.TotalSeconds;
        await mt.TranslateAsync("Bitte warten Sie einen Moment.", "de", ["zh", "pl", "en"], [], CancellationToken.None);
        notes.Add($"MT: product LlamaSharpMtProvider with {info.DisplayName} ({info.Parameters}), CPU backend, no context, one prompt per target. Load {loaded:F1} s, first translation {load.Elapsed.TotalSeconds - loaded:F1} s.");
        return mt;
    }

    private static async Task<string> LoadNoteAsync()
    {
        var self = Environment.ProcessId;
        Dictionary<int, (string Name, TimeSpan Cpu)> Snapshot() => Process.GetProcesses()
            .Select(p =>
            {
                try
                {
                    return (p.Id, Name: p.ProcessName, Cpu: p.TotalProcessorTime);
                }
                catch (Exception)
                {
                    return (p.Id, Name: p.ProcessName, Cpu: TimeSpan.Zero);
                }
            })
            .GroupBy(p => p.Id)
            .ToDictionary(g => g.Key, g => (g.First().Name, g.First().Cpu));

        var before = Snapshot();
        await Task.Delay(TimeSpan.FromSeconds(5));
        var after = Snapshot();
        var busy = after.Where(kv => kv.Key != self && before.TryGetValue(kv.Key, out var b) && b.Name == kv.Value.Name)
            .Select(kv => (kv.Value.Name, Cores: (kv.Value.Cpu - before[kv.Key].Cpu).TotalSeconds / 5))
            .GroupBy(x => x.Name)
            .Select(g => (Name: g.Key, Cores: g.Sum(x => x.Cores)))
            .Where(x => x.Cores >= 0.15)
            .OrderByDescending(x => x.Cores)
            .Take(6)
            .ToList();
        return busy.Count == 0
            ? "Other load before the route: no process used 0.15 logical CPUs or more over 5 s."
            : $"Other load before the route, over 5 s, out of {Environment.ProcessorCount} logical CPUs: {string.Join(", ", busy.Select(x => $"{x.Name} {x.Cores:F2}"))} (sum {busy.Sum(x => x.Cores):F2}).";
    }

    private static Task<Piece> TranslateAsync(IMtProvider mt, string text, string src, Stopwatch clock) => Task.Run(async () =>
    {
        var targets = Room.Where(l => !string.Equals(l, src, StringComparison.OrdinalIgnoreCase)).ToList();
        var source = SimplifiedChinese.IsChinese(src) ? SimplifiedChinese.Normalize(text) : text;
        try
        {
            var map = await mt.TranslateAsync(source, src, targets, [], CancellationToken.None);
            return new Piece(src, Simplified(map), clock.Elapsed.TotalSeconds, null);
        }
        catch (Exception e)
        {
            return new Piece(src, new Dictionary<string, string>(), clock.Elapsed.TotalSeconds, e.Message);
        }
    });

    private static Dictionary<string, string> Simplified(IReadOnlyDictionary<string, string> map) =>
        map.ToDictionary(kv => kv.Key, kv => SimplifiedChinese.IsChinese(kv.Key) ? SimplifiedChinese.Normalize(kv.Value) : kv.Value);

    private static Dictionary<string, string> Join(IEnumerable<IReadOnlyDictionary<string, string>> parts)
    {
        var list = parts.ToList();
        return Room
            .Select(l => (Lang: l, Text: string.Join(" ", list.Select(p => p.GetValueOrDefault(l, "")).Where(t => t.Length > 0))))
            .Where(x => x.Text.Length > 0)
            .ToDictionary(x => x.Lang, x => x.Text);
    }

    private static async Task<List<E2eRun>> LocalAsync(string route, Func<List<double>, IAsrProvider> make, IReadOnlyList<Clip> clips, IMtProvider mt)
    {
        await Feeder.RunClipAsync(make, clips[0], Room, realtime: false);
        var runs = new List<E2eRun>();
        foreach (var clip in clips)
        {
            var pieces = new List<Task<Piece>>();
            var run = await Feeder.RunClipAsync(make, clip, Room, realtime: true, onTranscript: (t, clock) =>
            {
                if (t.IsFinal && t.Text.Trim().Length > 0)
                    pieces.Add(TranslateAsync(mt, t.Text.Trim(), t.SrcLang, clock));
            });
            var done = await Task.WhenAll(pieces);
            foreach (var error in done.Select(p => p.Error).OfType<string>())
                Console.WriteLine($"   {clip.Id}: translation failed: {error}");
            var result = new E2eRun(route, clip, run.Text, run.Langs, Join(done.Select(p => p.Map)),
                done.Length == 0 ? null : done.Max(p => p.DoneAt) - run.SpeechEndAt, run.FinalAfterEnd);
            runs.Add(result);
            Console.WriteLine($"   {route} {clip.Id}: {result.LatencySeconds:F2} s (ASR {result.AsrSeconds:F2} s) \"{result.Transcript}\"");
        }

        return runs;
    }

    private sealed class Heard(AsrEvent.Transcript last, double firstAt)
    {
        public AsrEvent.Transcript Last { get; set; } = last;

        public double FirstAt { get; } = firstAt;

        public double LastAt { get; set; } = firstAt;
    }

    private static async Task<List<E2eRun>> GladiaAsync(IMtProvider mt, IReadOnlyList<Clip> clips, List<string> notes)
    {
        using var engine = new GladiaEngine(translation: true);
        var runs = new List<E2eRun>();
        var failures = 0;
        foreach (var clip in clips)
        {
            var utterances = new Dictionary<string, Heard>();
            var pieces = new List<Task<Piece>>();
            ClipRun run;
            try
            {
                run = await Feeder.RunClipAsync(engine.Provider, clip, Room, realtime: false, engine.PaddingSeconds, paced: true, onTranscript: (t, clock) =>
                {
                    if (!t.IsFinal)
                        return;
                    var at = clock.Elapsed.TotalSeconds;
                    if (utterances.TryGetValue(t.UtteranceId, out var known))
                    {
                        known.Last = t;
                        known.LastAt = at;
                        return;
                    }

                    utterances[t.UtteranceId] = new Heard(t, at);
                    if (t.Text.Trim().Length > 0)
                        pieces.Add(TranslateAsync(mt, t.Text.Trim(), t.SrcLang, clock));
                });
            }
            catch (Exception e)
            {
                Console.WriteLine($"   {clip.Id}: Gladia failed: {e.Message}");
                if (++failures >= 3)
                    throw new InvalidOperationException("Gladia failed on 3 clips. Stopping so that no more audio is billed.", e);
                continue;
            }

            var done = await Task.WhenAll(pieces);
            var ordered = utterances.Values.OrderBy(u => u.FirstAt).ToList();
            var transcript = string.Join(" ", ordered.Select(u => u.Last.Text.Trim()).Where(t => t.Length > 0));
            var langs = ordered.Select(u => u.Last.SrcLang).ToList();
            var native = Join(ordered.Select(u => (IReadOnlyDictionary<string, string>)Simplified(u.Last.Translations ?? new Dictionary<string, string>())));
            runs.Add(new E2eRun("A", clip, transcript, langs, native,
                ordered.Count == 0 ? null : ordered.Max(u => u.LastAt) - run.SpeechEndAt));
            runs.Add(new E2eRun("B", clip, transcript, langs, Join(done.Select(p => p.Map)),
                done.Length == 0 ? null : done.Max(p => p.DoneAt) - run.SpeechEndAt,
                ordered.Count == 0 ? null : ordered.Max(u => u.FirstAt) - run.SpeechEndAt));
            Console.WriteLine($"   {clip.Id}: A {runs[^2].LatencySeconds:F2} s, B {runs[^1].LatencySeconds:F2} s \"{transcript}\"");
        }

        var fed = runs.Where(r => r.Route == "A").Sum(r => r.Clip.Seconds + engine.PaddingSeconds);
        notes.Add($"Gladia: {fed / 60:F1} min of audio sent (clips plus {engine.PaddingSeconds:F0} s of silence each), {runs.Count / 2} sessions, {failures} failed. Route A and route B share these sessions.");
        return runs;
    }
}
