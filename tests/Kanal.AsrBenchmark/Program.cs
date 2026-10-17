using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Kanal.AsrBenchmark;
using Kanal.Core.Models;
using Kanal.Core.Providers;
using Kanal.Providers.LocalAsr;

CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
Console.OutputEncoding = Encoding.UTF8;

var data = Arg("--data")
    ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Kanal", "asr-bench");
var modelsDir = Arg("--models")
    ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Kanal", "models");

var native = Path.Combine(AppContext.BaseDirectory, "runtimes", RuntimeInformation.RuntimeIdentifier, "native");
Environment.SetEnvironmentVariable("PATH", native + Path.PathSeparator + Environment.GetEnvironmentVariable("PATH"));

if (args.FirstOrDefault() == "probe")
{
    using var probe = Engine.Load(Engine.Default(modelsDir), args[1], 1, data);
    return 0;
}

if (args.FirstOrDefault() == "fetch")
{
    var model = AsrModelCatalog.Models[0];
    var downloads = new ModelDownloadManager(modelsDir);
    var missing = downloads.MissingParts(model.Parts);
    if (missing.Count > 0)
    {
        Console.WriteLine($"Downloading {model.Id} to {modelsDir}");
        await downloads.DownloadAsync(missing, null, CancellationToken.None);
    }

    if (Arg("--candidates") is { } candidates)
        await Engine.FetchCandidatesAsync(data, candidates.Split(','));
    if (!args.Contains("--no-clips"))
        await Corpus.FetchAsync(data, int.Parse(Arg("--clips") ?? "20"), List("--sets", string.Join(',', Corpus.Sets.Keys)));
    return 0;
}

var perGroup = int.Parse(Arg("--clips") ?? "1000");
var clips = Corpus.Load(data)
    .GroupBy(c => (c.Set, c.Lang))
    .SelectMany(g => g.Take(perGroup))
    .ToList();
if (clips.Count == 0)
{
    Console.Error.WriteLine($"No clips in {data}. Run: dotnet run -c Release --project tests/Kanal.AsrBenchmark -- fetch");
    return 1;
}

var providers = List("--provider", "cpu");
var threadLevels = List("--threads", "1,2,4,8").Select(int.Parse).ToArray();
var streamLevels = List("--streams", "1,2,4").Select(int.Parse).ToArray();
var phases = List("--phases", "accuracy,realtime,streams,threads");
var rtPerLanguage = int.Parse(Arg("--rt-clips") ?? "3");
var os = OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsMacOS() ? "macos" : "linux";
var burn = int.Parse(Arg("--burn") ?? "0");
var machine = $"{CpuName()} · {Environment.ProcessorCount} logical CPUs · {RuntimeInformation.OSDescription} ({RuntimeInformation.ProcessArchitecture})"
              + (burn > 0 ? $" · {burn} busy background threads" : "");
for (var i = 0; i < burn; i++)
    new Thread(() => { var x = 1.0; while (true) x = Math.Sqrt(x + 1); }) { IsBackground = true }.Start();
string[] autoRoom = [.. Corpus.Languages];

var fleurs = clips.Where(c => c.Set == "fleurs").ToList();
var sample = Corpus.Languages.SelectMany(l => fleurs.Where(c => c.Lang == l).Take(2)).ToList();
var realtimeClips = Corpus.Languages.SelectMany(l => fleurs.Where(c => c.Lang == l).Take(rtPerLanguage)).ToList();

var only = Arg("--model")?.Split(',');
var models = new[] { Engine.Default(modelsDir) }.Concat(Engine.Downloaded(data)).Append(Engine.Gladia)
    .Where(m => only?.Contains(m.Id) ?? m != Engine.Gladia)
    .ToList();
var rows = new List<Row>();
var clipRows = new List<ClipRow>();
var notes = new List<string>();
if (Arg("--note") is { } note)
    notes.Add(note);
var probes = new Dictionary<string, string?>();

void Add(string model, string provider, int threads, int streams, string set, string lang, string room,
    string metric, double value, string unit) =>
    rows.Add(new Row(os, machine, model, provider, threads, streams, burn, set, lang, room, metric, Math.Round(value, 4), unit));

Console.WriteLine(machine);
Console.WriteLine($"{clips.Count} clips: {string.Join(", ", clips.GroupBy(c => $"{c.Set}/{c.Lang}").Select(g => $"{g.Key} {g.Count()}"))}");

foreach (var model in models)
foreach (var provider in model == Engine.Gladia ? ["cloud"] : providers)
{
    var threads = Engine.DefaultThreads;
    if (provider is not ("cpu" or "cloud") && !probes.ContainsKey(provider))
        probes[provider] = Probe(provider);
    if (provider is not ("cpu" or "cloud") && probes[provider] is { } fallback)
    {
        notes.Add($"{model.Id} · {provider}: {fallback}");
        Console.WriteLine($"   {provider}: {fallback}");
        continue;
    }

    Console.WriteLine($"== {model.Id} · {provider} · {threads} threads");
    var baselineMb = Environment.WorkingSet / 1048576.0;
    using var memory = new PeakMemory();
    var load = Stopwatch.StartNew();
    Engine engine;
    try
    {
        engine = Engine.Load(model, provider, threads, data);
    }
    catch (Exception e)
    {
        notes.Add($"{model.Id} · {provider}: load failed: {e.Message}");
        Console.WriteLine($"   load failed: {e.Message}");
        continue;
    }

    Add(model.Id, provider, threads, 1, "-", "-", "-", "load_s", load.Elapsed.TotalSeconds, "s");
    Add(model.Id, provider, threads, 1, "-", "-", "-", "working_set_after_load_mb", Environment.WorkingSet / 1048576.0, "MB");
    Console.WriteLine($"   load {load.Elapsed.TotalSeconds:F2} s");
    Func<List<double>, IAsrProvider> make = engine.Provider;
    var rooms = engine.Cloud ? ["auto"] : engine.NeedsLanguage ? ["forced"] : new[] { "forced", "auto" };
    if (engine.NeedsLanguage)
        notes.Add($"{model.Id}: needs the clip language, so it has no auto room. The real-time phase uses a room of the clip language.");
    if (!engine.Cloud)
        await RunClipAsync(make, clips[0], [clips[0].Lang], realtime: false);

    if (phases.Contains("accuracy"))
    {
        foreach (var room in rooms)
        {
            var runs = new List<ClipRun>();
            foreach (var clip in clips)
                runs.Add(await RunClipAsync(make, clip, room == "forced" ? [clip.Lang] : autoRoom, realtime: false, engine.PaddingSeconds, engine.Cloud));
            Score(model.Id, provider, threads, room, runs);
            Console.WriteLine($"   {room}: RTF {runs.Sum(r => r.Seconds) / runs.Sum(r => r.FedSeconds):F3}");
        }
    }

    if (phases.Contains("realtime"))
    {
        var runs = new List<ClipRun>();
        foreach (var clip in realtimeClips)
            runs.Add(await RunClipAsync(make, clip, engine.NeedsLanguage ? [clip.Lang] : autoRoom, realtime: true, engine.PaddingSeconds, engine.Cloud));
        var decode = runs.SelectMany(r => r.DecodeMs).ToList();
        var finals = runs.Where(r => r.FinalAfterEnd is not null).Select(r => r.FinalAfterEnd!.Value).ToList();
        var partials = runs.Where(r => r.FirstAfterOnset is not null).Select(r => r.FirstAfterOnset!.Value).ToList();
        void Rt(string metric, double value, string unit) =>
            Add(model.Id, provider, threads, 1, "fleurs", "all", "auto-realtime", metric, value, unit);
        if (model.ChunkSeconds > 0)
            Rt("chunk_ms", model.ChunkSeconds * 1000, "ms");
        else
            Rt("vad_min_silence_ms", OfflineEngine.MinSilenceSeconds * 1000, "ms");
        Rt("decode_p50_ms", Scoring.Percentile(decode, 0.5), "ms");
        Rt("decode_p95_ms", Scoring.Percentile(decode, 0.95), "ms");
        Rt("decode_max_ms", decode.DefaultIfEmpty().Max(), "ms");
        if (model.ChunkSeconds > 0)
            Rt("decode_over_chunk_pct", 100.0 * decode.Count(d => d > model.ChunkSeconds * 1000) / Math.Max(1, decode.Count), "%");
        Rt("final_after_speech_end_p50_s", Scoring.Percentile(finals, 0.5), "s");
        Rt("final_after_speech_end_p95_s", Scoring.Percentile(finals, 0.95), "s");
        Rt("first_partial_after_onset_p50_s", Scoring.Percentile(partials, 0.5), "s");
        Rt("first_partial_after_onset_p95_s", Scoring.Percentile(partials, 0.95), "s");
        Rt("clips", runs.Count, "n");
        Console.WriteLine($"   real time: decode p95 {Scoring.Percentile(decode, 0.95):F0} ms, final p50 {Scoring.Percentile(finals, 0.5):F2} s");
    }

    if (phases.Contains("streams") && !engine.Cloud)
        foreach (var n in streamLevels)
            await StreamsAsync(model, provider, threads, n, make, engine.PaddingSeconds);

    engine.Dispose();
    Add(model.Id, provider, threads, 1, "-", "-", "-", "peak_working_set_mb", memory.PeakMb, "MB");
    Add(model.Id, provider, threads, 1, "-", "-", "-", "peak_delta_mb", memory.PeakMb - baselineMb, "MB");

    if (phases.Contains("accuracy") && model.Id == AsrModelCatalog.Models[0].Id && provider == "cpu")
        await ParityAsync(model, clips.Where(c => c.Set == "meeting-piper").ToList());
}

if (phases.Contains("threads"))
{
    var model = Engine.Default(modelsDir);
    foreach (var threads in threadLevels)
    {
        Console.WriteLine($"== {model.Id} · cpu · {threads} threads");
        var load = Stopwatch.StartNew();
        using var engine = Engine.Load(model, "cpu", threads, data);
        Add(model.Id, "cpu", threads, 1, "-", "-", "threads", "load_s", load.Elapsed.TotalSeconds, "s");
        Func<List<double>, IAsrProvider> make = engine.Provider;
        await RunClipAsync(make, sample[0], autoRoom, realtime: false);
        var runs = new List<ClipRun>();
        foreach (var clip in sample)
            runs.Add(await RunClipAsync(make, clip, autoRoom, realtime: false, engine.PaddingSeconds));
        var decode = runs.SelectMany(r => r.DecodeMs).ToList();
        Add(model.Id, "cpu", threads, 1, "fleurs", "all", "threads", "rtf", runs.Sum(r => r.Seconds) / runs.Sum(r => r.FedSeconds), "x");
        Add(model.Id, "cpu", threads, 1, "fleurs", "all", "threads", "decode_p50_ms", Scoring.Percentile(decode, 0.5), "ms");
        Add(model.Id, "cpu", threads, 1, "fleurs", "all", "threads", "decode_p95_ms", Scoring.Percentile(decode, 0.95), "ms");
        Console.WriteLine($"   RTF {runs.Sum(r => r.Seconds) / runs.Sum(r => r.FedSeconds):F3}");
    }
}

var stamp = DateTime.Now;
var basePath = Path.GetFullPath(Path.Combine(Arg("--out") ?? ".", $"kanal-asr-benchmark-{os}-{stamp:yyyyMMdd-HHmm}"));
var options = new JsonSerializerOptions
{
    WriteIndented = true,
    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
};
await File.WriteAllTextAsync(basePath + ".json", JsonSerializer.Serialize(new { generated = stamp, machine, notes, rows, clips = clipRows }, options));
await File.WriteAllTextAsync(basePath + ".md", Report.Build(stamp, machine, clips, rows, clipRows, notes));
Console.WriteLine($"Report: {basePath}.md");
Console.WriteLine($"Data:   {basePath}.json");
return 0;

void Score(string model, string provider, int threads, string room, List<ClipRun> runs)
{
    foreach (var group in runs.GroupBy(r => (r.Clip.Set, r.Clip.Lang)))
    {
        var (set, lang) = group.Key;
        var scored = group.Select(r => (Run: r, Score: Scoring.Errors(lang, r.Text, r.Clip.Reference),
            Numbers: Scoring.Errors(lang, r.Text, r.Clip.Reference, numbers: true))).ToList();
        var decode = group.SelectMany(r => r.DecodeMs).ToList();
        void A(string metric, double value, string unit) => Add(model, provider, threads, 1, set, lang, room, metric, value, unit);
        A(lang == "zh" ? "cer_pct" : "wer_pct", 100.0 * scored.Sum(s => s.Score.Errors) / Math.Max(1, scored.Sum(s => s.Score.ReferenceLength)), "%");
        A(lang == "zh" ? "cer_numbers_pct" : "wer_numbers_pct", 100.0 * scored.Sum(s => s.Numbers.Errors) / Math.Max(1, scored.Sum(s => s.Numbers.ReferenceLength)), "%");
        var plain = scored.Where(s => !Scoring.HasDigit(s.Run.Clip.Reference)).ToList();
        if (plain.Count > 0)
        {
            A(lang == "zh" ? "cer_no_digit_pct" : "wer_no_digit_pct", 100.0 * plain.Sum(s => s.Score.Errors) / Math.Max(1, plain.Sum(s => s.Score.ReferenceLength)), "%");
            A("clips_no_digit", plain.Count, "n");
        }

        A("rtf", group.Sum(r => r.Seconds) / group.Sum(r => r.FedSeconds), "x");
        A("decode_p50_ms", Scoring.Percentile(decode, 0.5), "ms");
        A("decode_p95_ms", Scoring.Percentile(decode, 0.95), "ms");
        A("empty_clips", group.Count(r => r.Text.Length == 0), "n");
        A("clips", group.Count(), "n");
        if (room == "auto")
        {
            var langs = group.SelectMany(r => r.Langs).ToList();
            A("lang_guess_pct", 100.0 * langs.Count(l => l == lang) / Math.Max(1, langs.Count), "%");
        }

        if (set.StartsWith("meeting"))
        {
            A("codes_total", group.Sum(r => Scoring.Codes(r.Clip.Reference).Count), "n");
            A("codes_kept_strict", group.Sum(r => Scoring.CodesKept(r.Clip.Reference, r.Text, loose: false)), "n");
            A("codes_kept_loose", group.Sum(r => Scoring.CodesKept(r.Clip.Reference, r.Text, loose: true)), "n");
        }

        foreach (var (run, score, numbers) in scored)
            clipRows.Add(new ClipRow(model, provider, room, set, lang, run.Clip.Id, run.Clip.Reference, run.Text,
                score.Errors, score.ReferenceLength, numbers.Errors, numbers.ReferenceLength, string.Join(",", run.Langs), Math.Round(run.Seconds / run.FedSeconds, 4)));
    }
}

async Task ParityAsync(BenchModel model, List<Clip> meeting)
{
    using var product = new NemotronAsrProvider(AsrModelCatalog.Models[0], new ModelDownloadManager(modelsDir));
    var same = 0;
    foreach (var clip in meeting)
    {
        var real = await RunClipAsync(_ => product, clip, autoRoom, realtime: false);
        var bench = clipRows.First(r => r.Model == model.Id && r.Provider == "cpu" && r.Room == "auto" && r.Id == clip.Id && r.Set == clip.Set);
        if (real.Text == bench.Hypothesis)
            same++;
        else
            notes.Add($"parity: {clip.Id} differs. Product: \"{real.Text}\". Benchmark: \"{bench.Hypothesis}\".");
    }

    Add(model.Id, "cpu", Engine.DefaultThreads, 1, "meeting-piper", "all", "auto", "parity_identical", same, "n");
    Add(model.Id, "cpu", Engine.DefaultThreads, 1, "meeting-piper", "all", "auto", "parity_clips", meeting.Count, "n");
    Console.WriteLine($"   parity with NemotronAsrProvider: {same}/{meeting.Count} identical");
}

async Task StreamsAsync(BenchModel model, string provider, int threads, int n, Func<List<double>, IAsrProvider> make, double padding)
{
    var wall = Stopwatch.StartNew();
    var all = await Task.WhenAll(Enumerable.Range(0, n).Select(i => Task.Run(async () =>
    {
        var runs = new List<ClipRun>();
        for (var k = 0; k < sample.Count; k++)
            runs.Add(await RunClipAsync(make, sample[(k + i) % sample.Count], autoRoom, realtime: false, padding));
        return runs;
    })));
    var runs = all.SelectMany(r => r).ToList();
    var rtf = runs.Select(r => r.Seconds / r.FedSeconds).ToList();
    var decode = runs.SelectMany(r => r.DecodeMs).ToList();
    void S(string metric, double value, string unit) => Add(model.Id, provider, threads, n, "fleurs", "all", "streams", metric, value, unit);
    S("rtf_mean", rtf.Average(), "x");
    S("rtf_p95", Scoring.Percentile(rtf, 0.95), "x");
    S("decode_p50_ms", Scoring.Percentile(decode, 0.5), "ms");
    S("decode_p95_ms", Scoring.Percentile(decode, 0.95), "ms");
    S("throughput_x_realtime", runs.Sum(r => r.Clip.Seconds) / wall.Elapsed.TotalSeconds, "x");
    Console.WriteLine($"   {n} streams: mean RTF {rtf.Average():F3}, p95 {Scoring.Percentile(rtf, 0.95):F3}");
}

static async Task<ClipRun> RunClipAsync(Func<List<double>, IAsrProvider> make, Clip clip, string[] room, bool realtime, double padding = 0, bool paced = false)
{
    const int frame = Corpus.Rate / 50;
    var decodeMs = new List<double>();
    var session = await make(decodeMs).StartAsync(new AsrSessionOptions(Corpus.Rate, room), CancellationToken.None);
    var clock = Stopwatch.StartNew();
    var events = new List<(double At, AsrEvent.Transcript Event)>();
    var reader = Task.Run(async () =>
    {
        await foreach (var e in session.Events)
            if (e is AsrEvent.Transcript t)
                events.Add((clock.Elapsed.TotalSeconds, t));
            else if (e is AsrEvent.Error error)
                Console.WriteLine($"   {clip.Id}: {error.Message}");
    });

    var samples = clip.Samples;
    var onset = Scoring.SpeechOnset(samples, frame);
    var end = Scoring.SpeechEnd(samples, frame);
    var total = samples.Length + (int)((realtime ? 4 : paced ? padding : 0) * Corpus.Rate);
    double onsetAt = 0, endAt = 0;
    for (var i = 0; i < total; i += frame)
    {
        if (realtime || paced)
        {
            var wait = TimeSpan.FromSeconds(i / (double)Corpus.Rate) - clock.Elapsed;
            if (wait > TimeSpan.Zero)
                await Task.Delay(wait);
        }

        if (i == onset)
            onsetAt = clock.Elapsed.TotalSeconds;
        var chunk = i < samples.Length
            ? PcmBytes(samples.AsSpan(i, Math.Min(frame, samples.Length - i)))
            : new byte[frame * 2];
        await session.PushAudioAsync(chunk);
        if (i + frame == end || (i < end && i + frame > end))
            endAt = clock.Elapsed.TotalSeconds;
    }

    await session.DisposeAsync();
    await reader;
    var seconds = clock.Elapsed.TotalSeconds;
    var finals = events.Where(e => e.Event.IsFinal).ToList();
    return new ClipRun(
        clip,
        string.Join(" ", finals.Select(f => f.Event.Text)).Trim(),
        finals.Select(f => f.Event.SrcLang).ToList(),
        seconds,
        decodeMs,
        realtime && events.Count > 0 ? events[0].At - onsetAt : null,
        realtime && finals.Count > 0 ? finals[^1].At - endAt : null,
        padding);
}

string? Probe(string provider)
{
    var info = new ProcessStartInfo(Environment.ProcessPath!, ["probe", provider, "--models", modelsDir, "--data", data])
    {
        RedirectStandardError = true,
        RedirectStandardOutput = true,
    };
    using var process = Process.Start(info)!;
    var output = process.StandardError.ReadToEnd() + process.StandardOutput.ReadToEnd();
    process.WaitForExit();
    var line = output.Split('\n').FirstOrDefault(l => l.Contains("Fallback") || l.Contains("not available") || l.Contains("rror"));
    return process.ExitCode != 0 ? $"probe exited with {process.ExitCode}: {output.Trim()}" : line?.Trim();
}

static byte[] PcmBytes(ReadOnlySpan<short> samples) => Kanal.Audio.PcmConvert.ShortsToBytes(samples);

static string CpuName()
{
    try
    {
        if (OperatingSystem.IsWindows())
            return Microsoft.Win32.Registry.GetValue(
                @"HKEY_LOCAL_MACHINE\HARDWARE\DESCRIPTION\System\CentralProcessor\0", "ProcessorNameString", null)?.ToString()?.Trim() ?? "?";
        var info = new ProcessStartInfo(OperatingSystem.IsMacOS() ? "sysctl" : "sh",
            OperatingSystem.IsMacOS() ? "-n machdep.cpu.brand_string" : "-c \"grep -m1 'model name' /proc/cpuinfo | cut -d: -f2\"")
        { RedirectStandardOutput = true };
        using var process = Process.Start(info)!;
        return process.StandardOutput.ReadToEnd().Trim();
    }
    catch (Exception)
    {
        return RuntimeInformation.ProcessArchitecture.ToString();
    }
}

string? Arg(string name) => args.Select((a, i) => (a, i)).Where(x => x.a == name && x.i + 1 < args.Length)
    .Select(x => args[x.i + 1]).LastOrDefault();

string[] List(string name, string fallback) => (Arg(name) ?? fallback).Split(',', StringSplitOptions.RemoveEmptyEntries);

[ExcludeFromCodeCoverage]
internal sealed record ClipRun(
    Clip Clip,
    string Text,
    IReadOnlyList<string> Langs,
    double Seconds,
    IReadOnlyList<double> DecodeMs,
    double? FirstAfterOnset,
    double? FinalAfterEnd,
    double PaddingSeconds)
{
    public double FedSeconds => Clip.Seconds + PaddingSeconds;
}

[ExcludeFromCodeCoverage]
internal sealed record Row(
    string Os, string Machine, string Model, string Provider, int Threads, int Streams, int BackgroundThreads,
    string Set, string Language, string Room, string Metric, double Value, string Unit);

[ExcludeFromCodeCoverage]
internal sealed record ClipRow(
    string Model, string Provider, string Room, string Set, string Language, string Id,
    string Reference, string Hypothesis, int Errors, int Length, int NumbersErrors, int NumbersLength, string Langs, double Rtf);

[ExcludeFromCodeCoverage]
internal sealed class PeakMemory : IDisposable
{
    private readonly Timer _timer;
    private long _peak = Environment.WorkingSet;

    public PeakMemory() => _timer = new Timer(_ => Interlocked.Exchange(ref _peak, Math.Max(Interlocked.Read(ref _peak), Environment.WorkingSet)), null, 0, 50);

    public double PeakMb => Interlocked.Read(ref _peak) / 1048576.0;

    public void Dispose() => _timer.Dispose();
}

[ExcludeFromCodeCoverage]
internal partial class Program;
