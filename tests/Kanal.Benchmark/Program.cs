using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Text;
using Kanal.Benchmark;
using Kanal.Core.Text;
using Kanal.Providers.LocalMt;

CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
Console.OutputEncoding = Encoding.UTF8;

var modelsDir = Arg("--models")
    ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Kanal", "models");
var levels = (Arg("--streams") ?? "1,2,4").Split(',').Select(int.Parse).ToArray();
var backend = typeof(Corpus).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
    .FirstOrDefault(a => a.Key == "LlamaBackend")?.Value ?? "?";

var candidates = LocalModelCatalog.Models
    .Select(m => (Name: m.DisplayName, Path: Path.Combine(modelsDir, m.FileName), Prefill: m.AssistantPrefill))
    .Where(c => File.Exists(c.Path))
    .Concat(Args("--gguf").Select(p => (Path.GetFileNameWithoutExtension(p), p, (string?)null)))
    .Concat(Args("--gguf-nothink").Select(p => (Path.GetFileNameWithoutExtension(p), p, (string?)LocalModelCatalog.QwenNoThink)))
    .ToList();

if (candidates.Count == 0)
{
    Console.Error.WriteLine($"No model found. Download one in Kanal, or pass --gguf <file>. Looked in {modelsDir}.");
    return 1;
}

var summary = new StringBuilder()
    .AppendLine($"# Kanal local MT benchmark — {DateTime.Now:yyyy-MM-dd HH:mm}")
    .AppendLine()
    .AppendLine($"Backend `{backend}` · {Environment.ProcessorCount} logical CPUs · {Environment.OSVersion} · " +
                $"{Corpus.Sentences.Count} sentences × 2 targets · sentences in flight {string.Join("/", levels)}")
    .AppendLine()
    .Append("| Model | Load s | chrF | Codes kept | Script leak | Empty |")
    .AppendJoin("", levels.Select(n => $" {n} in flight avg/p95 s | {n} in flight utt/s |"))
    .Append(" 1 target s | 2 targets s | 3 targets s |")
    .AppendLine()
    .Append("|---|---:|---:|---:|---:|---:|")
    .AppendJoin("", levels.Select(_ => "---:|---:|"))
    .Append("---:|---:|---:|")
    .AppendLine();
var details = new StringBuilder();

foreach (var (name, path, prefill) in candidates)
{
    Console.WriteLine($"== {name} ({path})");
    await using var mt = new LlamaSharpMtProvider(new LlamaSharpTextGenerator(path, prefill));

    var load = Stopwatch.StartNew();
    try
    {
        await mt.WarmUpAsync(CancellationToken.None);
    }
    catch (Exception e)
    {
        Console.WriteLine($"   load failed: {e.Message}");
        summary.AppendLine($"| {name} | load failed: {e.Message.ReplaceLineEndings(" ")} |");
        continue;
    }
    var loadSeconds = load.Elapsed.TotalSeconds;
    await TranslateAsync(mt, Corpus.Sentences[0]);

    List<Result>? firstPass = null;
    var perLevel = new List<string>();
    foreach (var n in levels)
    {
        var wall = Stopwatch.StartNew();
        var streams = await Task.WhenAll(Enumerable.Range(0, n).Select(_ => Task.Run(async () =>
        {
            var stream = new List<Result>();
            foreach (var sentence in Corpus.Sentences)
                stream.Add(await TranslateAsync(mt, sentence));
            return stream;
        })));
        var results = streams.SelectMany(r => r).ToList();
        var seconds = results.Select(r => r.Seconds).ToList();
        perLevel.Add($" {seconds.Average():F2} / {Scoring.Percentile(seconds, 0.95):F2} | {results.Count / wall.Elapsed.TotalSeconds:F2} |");
        Console.WriteLine($"   {n} in flight: avg {seconds.Average():F2} s, p95 {Scoring.Percentile(seconds, 0.95):F2} s");
        firstPass ??= streams[0];
    }

    var perTargets = new List<string>();
    foreach (var count in new[] { 1, 2, 3 })
    {
        var seconds = new List<double>();
        foreach (var sentence in Corpus.Sentences)
            seconds.Add((await TranslateAsync(mt, sentence, count)).Seconds);
        perTargets.Add($" {seconds.Average():F2} |");
        Console.WriteLine($"   {count} target(s): avg {seconds.Average():F2} s");
    }

    var scored = firstPass!.SelectMany(r => r.Targets.Select(target =>
    {
        var output = r.Output.GetValueOrDefault(target, "");
        return (r.Sentence, Target: target, Output: output, ChrF: Scoring.ChrF(output, r.Sentence.References[target]));
    })).ToList();
    var withCodes = scored.Where(s => Scoring.Codes(s.Sentence.Text).Count > 0).ToList();

    summary.Append($"| {name} | {loadSeconds:F1} | {scored.Average(s => s.ChrF):F1} | " +
                   $"{withCodes.Count(s => Scoring.KeepsCodes(s.Sentence.Text, s.Output))}/{withCodes.Count} | " +
                   $"{scored.Count(s => Scoring.LeaksScript(s.Target, s.Output))} | {scored.Count(s => s.Output.Length == 0)} |")
        .AppendJoin("", perLevel)
        .AppendJoin("", perTargets)
        .AppendLine();

    details.AppendLine().AppendLine($"## {name}").AppendLine()
        .AppendLine("| Source | → | Output | Reference | chrF |").AppendLine("|---|---|---|---|---:|");
    foreach (var s in scored)
        details.AppendLine($"| {s.Sentence.Text} | {s.Target} | {s.Output.ReplaceLineEndings(" ")} | {s.Sentence.References[s.Target]} | {s.ChrF:F1} |");
}

var report = summary.ToString() + details;
var file = Path.GetFullPath($"kanal-benchmark-{DateTime.Now:yyyyMMdd-HHmmss}.md");
await File.WriteAllTextAsync(file, report);
Console.WriteLine();
Console.WriteLine(summary);
Console.WriteLine($"Report: {file}");
return 0;

static async Task<Result> TranslateAsync(LlamaSharpMtProvider mt, Sentence sentence, int targetCount = 2)
{
    var targets = Corpus.Targets(sentence).Take(targetCount).ToList();
    var clock = Stopwatch.StartNew();
    IReadOnlyDictionary<string, string> output;
    try
    {
        output = await mt.TranslateAsync(sentence.Text, sentence.Lang, targets, [], CancellationToken.None);
    }
    catch (Exception e)
    {
        Console.WriteLine($"   translate failed: {e.Message}");
        output = new Dictionary<string, string>();
    }
    var seconds = clock.Elapsed.TotalSeconds;
    return new Result(sentence, seconds, targets, output.ToDictionary(
        kv => kv.Key, kv => SimplifiedChinese.IsChinese(kv.Key) ? SimplifiedChinese.Normalize(kv.Value) : kv.Value));
}

string? Arg(string name) => Args(name).LastOrDefault();

IEnumerable<string> Args(string name) =>
    args.Select((a, i) => (a, i)).Where(x => x.a == name && x.i + 1 < args.Length).Select(x => args[x.i + 1]);

internal sealed record Result(Sentence Sentence, double Seconds, IReadOnlyList<string> Targets, IReadOnlyDictionary<string, string> Output);
