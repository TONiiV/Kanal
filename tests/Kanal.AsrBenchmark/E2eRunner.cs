using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using System.Text.Encodings.Web;
using System.Text.Json;
using Kanal.Core.Models;
using Kanal.Core.Providers;
using Kanal.Core.Room;
using Kanal.Core.Text;
using Kanal.Providers.LocalAsr;
using Kanal.Providers.LocalMt;

namespace Kanal.AsrBenchmark;

[ExcludeFromCodeCoverage]
internal sealed record E2eOptions(
    string Data, string ModelsDir, string OutDir, string RunId, IReadOnlyList<string> Routes, int PerGroup,
    string? Note, IReadOnlyList<string>? Only, IReadOnlyList<string> Include, string? GladiaRuns);

[ExcludeFromCodeCoverage]
internal static class E2eRunner
{
    private const int MaxAttempts = 3;

    private static readonly string[] Room = [.. Corpus.Languages];

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private sealed record Piece(string Src, IReadOnlyDictionary<string, string> Map, double DoneAt, string? Error);

    private sealed record RunDto(string Route, string ClipId, string Transcript, string[] SrcLangs,
        Dictionary<string, string> Translations, double? LatencySeconds, double? AsrSeconds,
        double? OtherLoad = null, bool Rerun = false, DateTime? RanAt = null, string? Machine = null);

    private sealed record RouteFile(string Machine, List<string> Notes, List<RunDto> Runs);

    public static async Task<int> RunAsync(E2eOptions o)
    {
        var clips = Corpus.Load(o.Data, ["meeting-11labs"]).GroupBy(c => c.Lang).SelectMany(g => g.Take(o.PerGroup)).ToList();
        if (o.Only is not null)
        {
            var wanted = E2e.Only(clips.Select(c => c.Id), o.Only).ToHashSet();
            if (o.Only.Except(wanted).ToList() is { Count: > 0 } unknown)
            {
                Console.Error.WriteLine($"--only takes whole clip ids. Unknown: {string.Join(", ", unknown)}.");
                return 2;
            }

            clips = [.. clips.Where(c => wanted.Contains(c.Id))];
        }

        if (clips.Count == 0 && o.Routes.Count > 0)
        {
            Console.Error.WriteLine($"No meeting-11labs clips in {o.Data}. Run: fetch --sets meeting-11labs");
            return 1;
        }

        Directory.CreateDirectory(o.OutDir);
        var machine = $"{Feeder.CpuName()} · {Environment.ProcessorCount} logical CPUs · {RuntimeInformation.OSDescription} ({RuntimeInformation.ProcessArchitecture})";
        Console.WriteLine($"{machine}\n{clips.Count} clips, {clips.Sum(c => c.Seconds) / 60:F1} min of audio");

        using var monitor = new LoadMonitor();
        foreach (var route in o.Routes)
        {
            var notes = new List<string>();
            if (o.Note is not null)
                notes.Add(o.Note);
            await Task.Delay(TimeSpan.FromSeconds(7));
            var start = await monitor.WaitQuietAsync($"route {route}");
            notes.Add($"Other load when the route started: {start:F2} logical CPUs of {Environment.ProcessorCount} (limit to run a clip: {LoadMonitor.QuietBelow:F1}). Each clip waits for this limit, and it runs again if the load passes the limit during the clip.");
            List<E2eRun> runs;
            switch (route)
            {
                case "gladia":
                    using (var mt = await LoadMtAsync(o.ModelsDir, notes))
                        runs = await GladiaAsync(mt, clips, notes, monitor);
                    break;
                case "gladia-mt":
                    using (var mt = await LoadMtAsync(o.ModelsDir, notes))
                        runs = await ReplayAsync(mt, o.GladiaRuns, clips, notes, monitor);
                    break;
                case "nemotron":
                    using (var asr = new NemotronAsrProvider(AsrModelCatalog.Models[0], new ModelDownloadManager(o.ModelsDir)))
                    using (var mt = await LoadMtAsync(o.ModelsDir, notes))
                    {
                        await asr.WarmUpAsync(CancellationToken.None);
                        runs = await LocalAsync("C", _ => asr, clips, mt, notes, monitor);
                    }

                    break;
                case "qwen3":
                    var model = Engine.Downloaded(o.Data).First(m => m.Id == "qwen3-asr-0.6b-int8");
                    using (var engine = Engine.Load(model, "cpu", Engine.DefaultThreads, o.Data))
                    using (var mt = await LoadMtAsync(o.ModelsDir, notes))
                        runs = await LocalAsync("D", engine.Provider, clips, mt, notes, monitor);
                    break;
                default:
                    Console.Error.WriteLine($"Unknown route {route}. Known routes: gladia, gladia-mt, nemotron, qwen3.");
                    return 2;
            }

            runs = [.. runs.Select(r => r with { Machine = machine })];
            var path = RunPath(o, route);
            if (o.Only is not null && File.Exists(path))
            {
                var earlier = Read(path);
                var merged = E2e.Merge(earlier.Runs, runs);
                notes.InsertRange(0, earlier.Notes);
                notes.Add($"Clips {string.Join(", ", o.Only)} ({o.Only.Count} clips) ran again, and the new runs replace the earlier ones and carry the re-run mark."
                          + (merged.KeptEarlier.Count > 0 ? $" These runs failed again, so the earlier runs stay: {string.Join(", ", merged.KeptEarlier)}." : ""));
                runs = [.. merged.Runs];
            }

            await File.WriteAllTextAsync(path, JsonSerializer.Serialize(
                new RouteFile(machine, notes, [.. runs.Select(ToDto)]), Json));
        }

        var all = new List<E2eRun>();
        var allNotes = new List<string>();
        var files = Directory.GetFiles(o.OutDir, $"e2e-runs-{o.RunId}-*.json").Concat(o.Include).Order().ToList();
        foreach (var path in files)
        {
            var file = Read(path);
            allNotes.AddRange(file.Notes.Select(n => $"{Path.GetFileNameWithoutExtension(path)}: {n}"));
            all.AddRange(file.Runs);
        }

        if (all.GroupBy(r => (r.Route, r.Clip.Id)).FirstOrDefault(g => g.Count() > 1) is { } duplicate)
            throw new InvalidOperationException($"Route {duplicate.Key.Route} has two runs of {duplicate.Key.Item2} in the files {string.Join(", ", files)}.");

        var stamp = DateTime.Now;
        var basePath = Path.Combine(o.OutDir, $"kanal-e2e-{o.RunId}");
        allNotes.Insert(0, $"Run files in this report: {string.Join(", ", files.Select(Path.GetFileName))}.");
        await File.WriteAllTextAsync(basePath + ".md", E2eReport.Build(stamp, machine, all, allNotes));
        await File.WriteAllTextAsync(basePath + ".json", JsonSerializer.Serialize(new
        {
            generated = stamp,
            machine,
            files = files.Select(Path.GetFileName),
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
                r.OtherLoad,
                r.Rerun,
                r.RanAt,
                r.Machine,
                complete = E2e.IsComplete(r),
                pairs = E2e.Pairs(r),
            }),
        }, Json));
        Console.WriteLine($"Report: {basePath}.md");
        return 0;
    }

    private static string RunPath(E2eOptions o, string route) => Path.Combine(o.OutDir, $"e2e-runs-{o.RunId}-{route}.json");

    private static RunDto ToDto(E2eRun r) => new(r.Route, r.Clip.Id, r.Transcript, [.. r.SrcLangs],
        r.Translations.ToDictionary(kv => kv.Key, kv => kv.Value), r.LatencySeconds, r.AsrSeconds, r.OtherLoad, r.Rerun, r.RanAt, r.Machine);

    private static (List<string> Notes, List<E2eRun> Runs) Read(string path)
    {
        var file = JsonSerializer.Deserialize<RouteFile>(File.ReadAllText(path), Json)!;
        return (file.Notes, [.. file.Runs.Select(r => new E2eRun(r.Route,
            new Clip("meeting-11labs", r.ClipId[..2], r.ClipId, E2e.Reference(r.ClipId, r.ClipId[..2]), []),
            r.Transcript, r.SrcLangs, r.Translations, r.LatencySeconds, r.AsrSeconds, r.OtherLoad, r.Rerun, r.RanAt, r.Machine ?? file.Machine))]);
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
        notes.Add($"MT: product LlamaSharpMtProvider with {info.DisplayName} ({info.Parameters}), CPU backend, one prompt per target. Load {loaded:F1} s, first translation {load.Elapsed.TotalSeconds - loaded:F1} s. "
                  + "The tool mirrors MeetingSession: RoomState.ApplyTranscript, then RecentFinals(8) as context. In this base, MtPrompt.Build takes only the text and the target, so the provider ignores the context.");
        return mt;
    }

    private static AsrEvent.Transcript NormalizeChinese(AsrEvent.Transcript t) =>
        SimplifiedChinese.IsChinese(t.SrcLang) ? t with { Text = SimplifiedChinese.Normalize(t.Text) } : t;

    private static Task<Piece> TranslateAsync(RoomState room, IMtProvider mt, Utterance u, Stopwatch clock) => Task.Run(async () =>
    {
        try
        {
            var targets = room.Config.Languages.Where(l => !string.Equals(l, u.SrcLang, StringComparison.OrdinalIgnoreCase)).ToList();
            var context = room.RecentFinals(8, excludeId: u.Id);
            var map = await mt.TranslateAsync(u.SrcText, u.SrcLang, targets, context, CancellationToken.None);
            return new Piece(u.SrcLang, Simplified(map), clock.Elapsed.TotalSeconds, null);
        }
        catch (Exception e)
        {
            return new Piece(u.SrcLang, new Dictionary<string, string>(), clock.Elapsed.TotalSeconds, e.Message);
        }
    });

    private static Dictionary<string, string> Simplified(IReadOnlyDictionary<string, string> map) =>
        map.ToDictionary(kv => kv.Key, kv => SimplifiedChinese.IsChinese(kv.Key) ? SimplifiedChinese.Normalize(kv.Value) : kv.Value);

    private static async Task<List<E2eRun>> LocalAsync(string route, Func<List<double>, IAsrProvider> make, IReadOnlyList<Clip> clips,
        IMtProvider mt, List<string> notes, LoadMonitor monitor)
    {
        await Feeder.RunClipAsync(make, clips[0], Room, realtime: false);
        var room = new RoomState(new RoomConfig($"e2e-{route}", Room));
        var runs = new List<E2eRun>();
        var again = new List<string>();
        foreach (var clip in clips)
        {
            for (var attempt = 1; ; attempt++)
            {
                await monitor.WaitQuietAsync(clip.Id);
                var started = DateTime.UtcNow;
                var pieces = new List<Task<Piece>>();
                var run = await Feeder.RunClipAsync(make, clip, Room, realtime: true, onTranscript: (t, clock) =>
                {
                    var utterance = room.ApplyTranscript(NormalizeChinese(t));
                    if (t.IsFinal && utterance.SrcText.Trim().Length > 0)
                        pieces.Add(TranslateAsync(room, mt, utterance, clock));
                });
                var done = await Task.WhenAll(pieces);
                var load = monitor.MeanSince(started);
                if (load >= LoadMonitor.QuietBelow && attempt < MaxAttempts)
                {
                    Console.WriteLine($"   {route} {clip.Id}: other load {load:F1} logical CPUs during the clip, running it again.");
                    again.Add(clip.Id);
                    continue;
                }

                foreach (var error in done.Select(p => p.Error).OfType<string>())
                    Console.WriteLine($"   {clip.Id}: translation failed: {error}");
                var result = new E2eRun(route, clip, run.Text, run.Langs, E2e.Join(done.Select(p => p.Map)),
                    done.Length == 0 ? null : done.Max(p => p.DoneAt) - run.SpeechEndAt, run.FinalAfterEnd, load, RanAt: DateTime.Now);
                runs.Add(result);
                Console.WriteLine($"   {route} {clip.Id}: {result.LatencySeconds:F2} s (ASR {result.AsrSeconds:F2} s, load {load:F2}) \"{result.Transcript}\"");
                break;
            }
        }

        if (again.Count > 0)
            notes.Add($"{again.Count} clip runs were repeated inside this run because other load passed the limit: {string.Join(", ", again)}.");
        return runs;
    }

    private static async Task<List<E2eRun>> ReplayAsync(IMtProvider mt, string? gladiaRuns, IReadOnlyList<Clip> clips, List<string> notes, LoadMonitor monitor)
    {
        if (gladiaRuns is null)
            throw new InvalidOperationException("Route gladia-mt needs --gladia-runs <file> with the saved Gladia sessions.");
        var saved = Read(gladiaRuns).Runs.Where(r => r.Route == "B" && r.SrcLangs.Count == 1 && r.Transcript.Trim().Length > 0 && r.AsrSeconds is not null)
            .ToDictionary(r => r.Clip.Id);
        var room = new RoomState(new RoomConfig("e2e-B1", Room));
        var runs = new List<E2eRun>();
        foreach (var clip in clips.Where(c => saved.ContainsKey(c.Id)))
        {
            var source = saved[clip.Id];
            for (var attempt = 1; ; attempt++)
            {
                await monitor.WaitQuietAsync(clip.Id);
                var started = DateTime.UtcNow;
                var clock = Stopwatch.StartNew();
                var utterance = room.ApplyTranscript(NormalizeChinese(new AsrEvent.Transcript(
                    $"b1-{clip.Id}-{attempt}", "S01", source.Transcript, source.SrcLangs[0], 0, null, true, false, 1.0, null)));
                var piece = await TranslateAsync(room, mt, utterance, clock);
                var load = monitor.MeanSince(started);
                if (load >= LoadMonitor.QuietBelow && attempt < MaxAttempts)
                    continue;
                runs.Add(new E2eRun("B1", clip, source.Transcript, source.SrcLangs, E2e.Join([piece.Map]),
                    source.AsrSeconds!.Value + piece.DoneAt, source.AsrSeconds, load, RanAt: DateTime.Now));
                Console.WriteLine($"   B1 {clip.Id}: {runs[^1].LatencySeconds:F2} s (saved Gladia final {source.AsrSeconds:F2} s, load {load:F2})");
                break;
            }
        }

        notes.Add($"Replay of the saved Gladia sessions: {runs.Count} clips with one Gladia final each. The saved text and the saved time of the final are used; a Gladia session cost nothing here. Latency = saved Gladia final time + fresh MT time. The saved sessions of clips with several finals hold no per-final text or time, so those clips are not replayed.");
        return runs;
    }

    private sealed class Heard(AsrEvent.Transcript last, double firstAt)
    {
        public AsrEvent.Transcript Last { get; set; } = last;

        public double FirstAt { get; } = firstAt;

        public double LastAt { get; set; } = firstAt;
    }

    private static async Task<List<E2eRun>> GladiaAsync(IMtProvider mt, IReadOnlyList<Clip> clips, List<string> notes, LoadMonitor monitor)
    {
        using var engine = new GladiaEngine(translation: true);
        var room = new RoomState(new RoomConfig("e2e-gladia", Room));
        var runs = new List<E2eRun>();
        var failures = 0;
        foreach (var clip in clips)
        {
            await monitor.WaitQuietAsync(clip.Id);
            var started = DateTime.UtcNow;
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
                    var utterance = room.ApplyTranscript(NormalizeChinese(t with { Translations = null }));
                    if (utterance.SrcText.Trim().Length > 0)
                        pieces.Add(TranslateAsync(room, mt, utterance, clock));
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
            var load = monitor.MeanSince(started);
            var a = E2e.RouteA(utterances.Values.Select(u => new HeardUtterance(
                u.Last.Text, u.Last.SrcLang, u.FirstAt, u.LastAt,
                Simplified(u.Last.Translations ?? new Dictionary<string, string>()))), run.SpeechEndAt);
            var ordered = utterances.Values.Where(u => u.Last.Text.Trim().Length > 0).ToList();
            runs.Add(new E2eRun("A", clip, a.Transcript, a.SrcLangs, a.Translations, a.LatencySeconds, null, load, RanAt: DateTime.Now));
            runs.Add(new E2eRun("B", clip, a.Transcript, a.SrcLangs, E2e.Join(done.Select(p => p.Map)),
                done.Length == 0 ? null : done.Max(p => p.DoneAt) - run.SpeechEndAt,
                ordered.Count == 0 ? null : ordered.Max(u => u.FirstAt) - run.SpeechEndAt, load, RanAt: DateTime.Now));
            Console.WriteLine($"   {clip.Id}: A {runs[^2].LatencySeconds:F2} s, B {runs[^1].LatencySeconds:F2} s \"{a.Transcript}\"");
        }

        var fed = runs.Where(r => r.Route == "A").Sum(r => r.Clip.Seconds + engine.PaddingSeconds);
        var missing = runs.Where(r => r.Route == "A").Count(r => !E2e.IsComplete(r));
        notes.Add($"Gladia: {fed / 60:F1} min of audio sent (clips plus {engine.PaddingSeconds:F0} s of silence each), {runs.Count / 2} sessions, {failures} failed. Route A and route B share these sessions. Route A clips with a missing translation: {missing}.");
        return runs;
    }
}
