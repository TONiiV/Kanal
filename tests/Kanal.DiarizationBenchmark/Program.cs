using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Kanal.Audio;
using Kanal.Core.Meetings;
using Kanal.Core.Models;
using Kanal.Core.Workspaces;
using Kanal.DiarizationBenchmark;

CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
Console.OutputEncoding = Encoding.UTF8;

var data = Arg("--data")
    ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Kanal", "diarization-bench");
var modelsDir = Arg("--models")
    ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Kanal", "models");
var outDir = Arg("--out") ?? data;
var json = new JsonSerializerOptions { WriteIndented = true, NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals };
if (Arg("--threads") is { } threadArg)
    Engines.Threads = int.Parse(threadArg);
var machine = $"{CpuName()} · {Environment.ProcessorCount} logical CPUs · {RuntimeInformation.OSDescription} · {Engines.Threads} threads";
var segmentation = Arg("--segmentation") ?? Engines.ModelPath(modelsDir, data, DiarizationModelCatalog.DefaultSegmentation);
var storedIds = List("--meetings", "negotiation-6-clean,negotiation-6-room,mix-3-clean,mix-3-overlap-room,mix-4-clean,mix-4-overlap-room,mix-6-clean,mix-6-overlap-room,pl-4-clean,pl-4-overlap-room,pl-6-clean,pl-6-overlap-room");
var embeddings = DiarizationModelCatalog.Embeddings
    .Where(m => Arg("--embedding") is not { } only || only.Split(',').Contains(m.Id))
    .ToList();

switch (args.FirstOrDefault())
{
    case "voices":
        foreach (var voice in Corpus.Voices.Values.SelectMany(v => v))
        {
            var clip = Corpus.Synthesised(data, voice, Sample(voice.Lang));
            Console.WriteLine($"{voice.Id,-14} {voice.Gender} {Corpus.NativeRate(data, voice),6} Hz  {clip.Length / 16000.0,5:F1} s  F0 {Corpus.MedianF0(clip),4:F0} Hz");
        }

        return 0;
    case "real":
        return Real(args[1]);
    case "cloud":
        return CloudRun();
    case "rttm":
        return SherpaRttm(args[1]);
    case "report":
        var parts = args.Skip(1).Where(a => a.EndsWith(".json")).Select(a => JsonSerializer.Deserialize<Run>(File.ReadAllText(a), json)!).ToList();
        var merged = parts[0] with
        {
            Results = [.. parts.SelectMany(p => p.Results)],
            Costs = [.. parts.SelectMany(p => p.Costs)],
            Notes = [.. parts.SelectMany(p => p.Notes)],
            Details = [.. parts.SelectMany(p => p.Details ?? [])],
        };
        var md = Report.Build(merged);
        File.WriteAllText(Path.Combine(outDir, "kanal-diarization-benchmark-merged.md"), md);
        Console.WriteLine(md);
        return 0;
}

var languages = List("--lang", "zh,de,pl,mix");
var counts = List("--speakers", "2,3,4,6").Select(int.Parse).ToArray();
var variants = List("--variants", "clean,overlap,room,overlap-room");
var thresholds = List("--thresholds", "0.3,0.4,0.5,0.6,0.7,0.8,0.9").Select(float.Parse).ToArray();
var liveThresholds = List("--live-thresholds", "0.3,0.4,0.5,0.6,0.7").Select(double.Parse).ToArray();
var parity = int.Parse(Arg("--parity") ?? "2");
var candidateThresholds = Arg("--candidate-thresholds") is { } ct ? ct.Split(',').Select(float.Parse).ToArray() : thresholds;
var done = args.Contains("--skip-done") && Directory.Exists(outDir)
    ? Directory.GetFiles(outDir, "*.json").SelectMany(f => JsonSerializer.Deserialize<Run>(File.ReadAllText(f), json)!.Results.Select(x => x.Meeting)).ToHashSet()
    : [];
var results = new List<Result>();
var costs = new List<Cost>();
var notes = new List<string>();
var details = new List<string>();
var script = Arg("--script");
var until = Arg("--until") is { } u ? DateTime.Today + TimeSpan.Parse(u) : DateTime.MaxValue;
var stamp = DateTime.Now;
Console.WriteLine(machine);

foreach (var meeting in (script is null ? Corpus.Meetings(data, languages, counts, variants) : Corpus.Scripted(data, script)).Where(m => !done.Contains(m.Id)))
{
    if (DateTime.Now > until)
    {
        notes.Add($"The run stopped starting meetings at {until:HH:mm}.");
        break;
    }

    var wav = Path.Combine(data, "meetings", meeting.Id + ".wav");
    Directory.CreateDirectory(Path.GetDirectoryName(wav)!);
    using (var file = File.Create(wav))
        WavFile.Write(file, PcmConvert.ShortsToBytes([.. meeting.Samples.Select(s => (short)Math.Clamp(s * 32767f, short.MinValue, short.MaxValue))]), Corpus.Rate, 1);
    File.WriteAllLines(Path.ChangeExtension(wav, ".rttm"), meeting.Truth.Select(t =>
        $"SPEAKER {meeting.Id} 1 {t.Start:F3} {t.Seconds:F3} <NA> <NA> {t.Speaker} <NA> <NA>"));
    var overlapSeconds = Overlapped(meeting.Truth);
    Console.WriteLine($"== {meeting.Id}: {meeting.Seconds:F0} s, {meeting.Truth.Count} utterances ({meeting.Truth.Count(u => u.Seconds < 1)} under 1 s), {overlapSeconds:F1} s overlapped");

    foreach (var model in embeddings)
    {
        var embedding = Engines.ModelPath(modelsDir, data, model);
        var configs = new List<(string System, int? Clusters, float? Threshold)> { ("count", meeting.Speakers, null) };
        configs.AddRange((model == DiarizationModelCatalog.DefaultEmbedding ? thresholds : candidateThresholds).Select(t => ("auto", (int?)null, (float?)t)));
        foreach (var (system, clusters, threshold) in configs)
        {
            var before = Environment.WorkingSet / 1048576.0;
            using var memory = new PeakMemory();
            var (diarization, load) = Clock.Time(() => Engines.Load(segmentation, embedding, clusters, threshold));
            var (turns, seconds) = Clock.Time(() => Engines.Diarize(diarization, meeting.Samples));
            diarization.Dispose();
            var result = Score(meeting, model.Id, system, threshold ?? 0, turns);
            if (script is not null && (system == "count" || threshold is 0.5f or 0.8f))
                details.Add(Confusion(meeting, $"{model.Id}, {(system == "count" ? "count given" : $"threshold {threshold:0.0}")}", Scoring.Attribute(meeting.Truth, turns)));
            results.Add(result with { Rtf = seconds / meeting.Seconds });
            costs.Add(new Cost(model.Id, meeting.Id, meeting.Seconds, load, seconds, memory.PeakMb - before, memory.PeakMb));
            Console.WriteLine($"   {model.Id,-28} {system,-5} {threshold,4} DER {result.Der * 100,5:F1} % est {result.Estimated} acc {result.Accuracy * 100,5:F1} % RTF {seconds / meeting.Seconds:F3}");
        }

        var (vectors, embedSeconds) = Clock.Time(() => Engines.Embeddings(embedding, meeting));
        foreach (var t in liveThresholds)
        {
            var tags = Engines.Live(meeting, vectors, t);
            if (script is not null && Math.Abs(t - LiveSpeakerLabels.DefaultThreshold) < 1e-6)
                details.Add(Confusion(meeting, $"{model.Id}, live layer, threshold {t:0.0}", tags));
            results.Add(Attribution(meeting, model.Id, "live", t, tags) with { Rtf = embedSeconds / meeting.Truth.Sum(u => u.Seconds) });
        }

        if (Scoring.Live(results, model.Id, LiveSpeakerLabels.DefaultThreshold) is { } live)
            Console.WriteLine($"   {model.Id,-28} live  {LiveSpeakerLabels.DefaultThreshold:0.0}  acc {live.Accuracy * 100,5:F1} % speakers {live.Estimated}");
    }

    if (parity-- > 0)
    {
        var model = DiarizationModelCatalog.DefaultEmbedding;
        var embedding = Engines.ModelPath(modelsDir, data, model);
        foreach (var count in new int?[] { meeting.Speakers, null })
        {
            var product = Engines.Product(segmentation, embedding, wav, count);
            using var bench = Engines.Load(segmentation, embedding, count, null);
            var mine = Engines.Diarize(bench, meeting.Samples);
            var same = product.Count == mine.Count && product.Zip(mine).All(p => Math.Abs(p.First.StartSeconds - p.Second.StartSeconds) < 0.02 && p.First.Speaker == p.Second.Speaker);
            notes.Add($"Parity {meeting.Id}, count {count?.ToString() ?? "none"}: SherpaSpeakerDiarizer gave {product.Count} turns, the benchmark engine {mine.Count}; {(same ? "identical" : "different")}.");
            Console.WriteLine($"   {notes[^1]}");
        }
    }

    Save();
}

var stem = Save();
File.WriteAllText(stem + ".md", Report.Build(new Run(stamp, machine, Engines.DefaultThreshold, Corpus.RoomDescription, results, costs, notes, details)));
Console.WriteLine($"Wrote {stem}.md and .json");
return 0;

string Save()
{
    if (Corpus.ElevenLabsCharacters > 0)
        Console.WriteLine($"ElevenLabs characters used in this run: {Corpus.ElevenLabsCharacters}");
    Directory.CreateDirectory(outDir);
    var path = Path.Combine(outDir, $"kanal-diarization-benchmark-{(script is null ? string.Join('-', languages) : Path.GetFileNameWithoutExtension(script))}-{stamp:yyyyMMdd-HHmm}");
    File.WriteAllText(path + ".json", JsonSerializer.Serialize(new Run(stamp, machine, Engines.DefaultThreshold, Corpus.RoomDescription, results, costs, notes, details), json));
    return path;
}

Result Score(Meeting meeting, string model, string system, double threshold, IReadOnlyList<SpeakerTurn> turns)
{
    var hypothesis = turns.Select(t => new Segment(t.StartSeconds, t.EndSeconds, t.Speaker.ToString(CultureInfo.InvariantCulture))).ToList();
    var der = Scoring.Der(meeting.Truth, hypothesis, 0.25);
    var bare = Scoring.Der(meeting.Truth, hypothesis, 0);
    return Attribution(meeting, model, system, threshold, Scoring.Attribute(meeting.Truth, turns)) with
    {
        Der = der.Der,
        Missed = der.Rate(der.Missed),
        FalseAlarm = der.Rate(der.FalseAlarm),
        Confusion = der.Rate(der.Confusion),
        DerNoCollar = bare.Der,
        Estimated = turns.Select(t => t.Speaker).Distinct().Count(),
    };
}

static Result Attribution(Meeting meeting, string model, string system, double threshold, string?[] tags)
{
    var pairs = meeting.Truth.Select((u, i) => (Truth: u.Speaker, Tag: tags[i])).ToList();
    var map = Scoring.MapTags(pairs);
    var (correct, total) = Scoring.Accuracy(pairs, map);
    var (shortCorrect, shortTotal) = Scoring.Accuracy(pairs.Where((_, i) => meeting.Truth[i].Seconds < 1), map);
    var (longCorrect, longTotal) = Scoring.Accuracy(pairs.Where((_, i) => meeting.Truth[i].Seconds >= 1), map);
    return new Result(meeting.Id, meeting.Lang, meeting.Speakers, meeting.Overlap, meeting.Room, model, system, threshold,
        double.NaN, double.NaN, double.NaN, double.NaN, double.NaN,
        tags.Where(t => t is not null).Distinct().Count(), tags.Where(t => t is not null).Distinct().Count(),
        correct, total, shortCorrect, shortTotal, longCorrect, longTotal, 0);
}

static string Confusion(Meeting meeting, string label, string?[] tags)
{
    var pairs = meeting.Truth.Select((u, i) => (Truth: u.Speaker, Tag: tags[i])).ToList();
    var map = Scoring.MapTags(pairs);
    var columns = pairs.Select(p => p.Tag ?? "none").GroupBy(t => t).OrderByDescending(g => g.Count()).Select(g => g.Key).ToList();
    var md = new StringBuilder()
        .AppendLine($"#### {meeting.Id}: {label}").AppendLine()
        .AppendLine("| Speaker | Utterances | Correct % | " + string.Join(" | ", columns.Select(c => map.TryGetValue(c, out var to) ? $"{c} (→ {to})" : c)) + " |")
        .AppendLine("|---|---:|---:|" + string.Concat(columns.Select(_ => "---:|")));
    foreach (var speaker in meeting.Truth.Select(u => u.Speaker).Distinct())
    {
        var mine = pairs.Where(p => p.Truth == speaker).ToList();
        var (correct, total) = Scoring.Accuracy(mine, map);
        md.AppendLine($"| {speaker} | {total} | {correct * 100.0 / total:F0} | " + string.Join(" | ", columns.Select(c => mine.Count(p => (p.Tag ?? "none") == c))) + " |");
    }

    return md.ToString();
}

static double Overlapped(IReadOnlyList<Segment> truth)
{
    double sum = 0;
    for (var i = 0; i < truth.Count; i++)
    for (var j = i + 1; j < truth.Count; j++)
        sum += Math.Max(0, Math.Min(truth[i].End, truth[j].End) - Math.Max(truth[i].Start, truth[j].Start));
    return sum;
}

int Real(string meetingFolder)
{
    var audio = Path.Combine(meetingFolder, WorkspaceStore.AudioFileName);
    var utterances = TranscriptLog.Read(Path.Combine(meetingFolder, TranscriptLog.FileName));
    using var file = File.OpenRead(audio);
    var pcm = PcmConvert.BytesToShorts(WavFile.Read(file).Pcm16);
    var samples = pcm.Select(s => s / 32768f).ToArray();
    var seconds = samples.Length / (double)Corpus.Rate;
    var lines = new List<string>
    {
        $"Real recording: {seconds / 60:F1} min, {utterances.Count} utterances, {machine}.",
        "",
        "| Embedding | Count given | Threshold | Speakers found | Tags after Assign | Share of diarized speech per speaker (largest first) | RTF | Load s | Peak working set MB |",
        "|---|---:|---:|---:|---:|---|---:|---:|---:|",
    };
    var configs = new List<(int? Count, float? Threshold)> { (null, null), (3, null) };
    configs.AddRange(List("--thresholds", "0.6,0.7,0.8,0.9").Select(t => ((int?)null, (float?)float.Parse(t))));
    foreach (var model in embeddings)
    foreach (var (count, threshold) in configs)
    {
        var embedding = Engines.ModelPath(modelsDir, data, model);
        using var memory = new PeakMemory();
        var (diarization, load) = Clock.Time(() => Engines.Load(segmentation, embedding, count, threshold));
        var (turns, took) = Clock.Time(() => Engines.Diarize(diarization, samples));
        diarization.Dispose();
        var total = turns.Sum(t => t.EndSeconds - t.StartSeconds);
        var shares = turns.GroupBy(t => t.Speaker).Select(g => g.Sum(t => t.EndSeconds - t.StartSeconds) / total).OrderDescending().ToList();
        var tags = SpeakerReidentification.Assign(utterances, turns).Values.Distinct().Count();
        var shown = string.Join(" / ", shares.Take(8).Select(s => $"{s * 100:F0}")) + (shares.Count > 8 ? $" / … ({shares.Count - 8} more, {shares.Skip(8).Sum() * 100:F0} % together)" : "");
        lines.Add($"| {model.Id} | {count?.ToString() ?? "–"} | {threshold?.ToString("0.0") ?? $"{Engines.DefaultThreshold:0.0} (default)"} | {shares.Count} | {tags} | {shown} | {took / seconds:F3} | {load:F2} | {memory.PeakMb:F0} |");
        Console.WriteLine(lines[^1]);
    }

    var path = Path.Combine(outDir, $"kanal-diarization-real-{DateTime.Now:yyyyMMdd-HHmm}.md");
    File.WriteAllLines(path, lines);
    Console.WriteLine($"Wrote {path}");
    return 0;
}

int CloudRun()
{
    var ids = storedIds;
    var services = List("--services", $"{Cloud.Gladia},{Cloud.Scribe}");
    var realFolder = Arg("--real");
    var realHint = int.Parse(Arg("--real-hint") ?? "3");
    var meetings = ids.Select(Stored).ToList();
    var forbidden = new HashSet<string>();
    var gates = services.ToDictionary(s => s, _ => new SemaphoreSlim(3));
    var cloudResults = new List<Result>();
    var cloudDetails = new List<string>();
    var cloudNotes = new List<string>();
    var sent = services.ToDictionary(s => s, _ => 0.0);
    var realLines = new List<string>();

    async Task<IReadOnlyList<SpeakerTurn>?> Call(string service, string id, string wav, int? hint, string[] languages, double seconds)
    {
        await gates[service].WaitAsync();
        try
        {
            lock (forbidden)
                if (forbidden.Contains(service))
                    return null;
            var watch = Stopwatch.StartNew();
            var turns = await Cloud.Diarize(service, data, id, wav, hint, languages);
            lock (sent)
                sent[service] += seconds;
            Console.WriteLine($"   {service,-22} {id,-24} hint {hint?.ToString() ?? "none",-4} {turns.Select(t => t.Speaker).Distinct().Count()} speakers, {watch.Elapsed.TotalSeconds:F0} s");
            return turns;
        }
        catch (ForbiddenException e)
        {
            lock (forbidden)
                if (forbidden.Add(service))
                    lock (cloudNotes)
                        cloudNotes.Add($"{service} stopped: {e.Message}");
            Console.WriteLine($"   {service}: {e.Message}");
            return null;
        }
        catch (Exception e)
        {
            lock (cloudNotes)
                cloudNotes.Add($"{service} {id} hint {hint?.ToString() ?? "none"} failed: {e.Message}");
            Console.WriteLine($"   {service} {id}: {e.Message}");
            return null;
        }
        finally
        {
            gates[service].Release();
        }
    }

    var jobs = new List<Task>();
    foreach (var meeting in meetings)
    foreach (var service in services)
    foreach (var hint in new int?[] { meeting.Speakers, null })
        jobs.Add(Task.Run(async () =>
        {
            var languages = meeting.Lang == "mix" ? new[] { "zh", "de", "pl" } : [meeting.Lang];
            var turns = await Call(service, meeting.Id, Path.Combine(data, "meetings", meeting.Id + ".wav"), hint, languages, meeting.Seconds);
            if (turns is null)
                return;
            var result = Score(meeting, service, hint is null ? "none" : "hint", 0, turns);
            lock (cloudResults)
            {
                cloudResults.Add(result);
                if (meeting.Id.StartsWith("negotiation", StringComparison.Ordinal))
                    cloudDetails.Add(Confusion(meeting, $"{service}, {(hint is null ? "no hint" : $"hint {hint}")}", Scoring.Attribute(meeting.Truth, turns)));
            }
        }));

    if (realFolder is not null)
    {
        var audio = Path.Combine(realFolder, WorkspaceStore.AudioFileName);
        var utterances = TranscriptLog.Read(Path.Combine(realFolder, TranscriptLog.FileName));
        double seconds;
        using (var file = File.OpenRead(audio))
            seconds = WavFile.Read(file).Pcm16.Length / 2.0 / Corpus.Rate;
        realLines.Add($"Real recording: {seconds / 60:F1} min, {utterances.Count} utterances.");
        realLines.Add("");
        realLines.Add("| Service | Hint | Speakers found | Tags after Assign | Share of diarized speech per speaker (largest first) |");
        realLines.Add("|---|---:|---:|---:|---|");
        foreach (var service in services)
        foreach (var hint in new int?[] { realHint, null })
            jobs.Add(Task.Run(async () =>
            {
                var turns = await Call(service, "real", audio, hint, ["zh", "de", "pl", "en"], seconds);
                if (turns is null)
                    return;
                var total = turns.Sum(t => t.EndSeconds - t.StartSeconds);
                var shares = turns.GroupBy(t => t.Speaker).Select(g => g.Sum(t => t.EndSeconds - t.StartSeconds) / total).OrderDescending().ToList();
                var tags = SpeakerReidentification.Assign(utterances, turns).Values.Distinct().Count();
                lock (realLines)
                    realLines.Add($"| {service} | {hint?.ToString() ?? "–"} | {shares.Count} | {tags} | {string.Join(" / ", shares.Select(s => $"{s * 100:F0}"))} |");
            }));
    }

    Task.WaitAll(jobs);

    var sherpa = Directory.GetFiles(Arg("--sherpa") ?? Path.Combine(data, "runs"), "kanal-diarization-benchmark-*.json").Order()
        .SelectMany(f => JsonSerializer.Deserialize<Run>(File.ReadAllText(f), json)!.Results)
        .Where(r => r.Model == DiarizationModelCatalog.DefaultEmbedding.Id && ids.Contains(r.Meeting)
                    && (r.System == "count" || (r.System == "auto" && Math.Abs(r.Threshold - 0.8) < 1e-3)))
        .GroupBy(r => (r.Meeting, r.System))
        .Select(g => g.Last() with { Model = "sherpa CAM++", System = g.Key.System == "count" ? "hint" : "none" })
        .ToList();
    var all = sherpa.Concat(cloudResults).ToList();
    var columns = new[] { "sherpa CAM++" }.Concat(services).SelectMany(s => new[] { (Service: s, System: "hint"), (Service: s, System: "none") }).ToList();
    var md = new StringBuilder()
        .AppendLine("Each cell: DER % (0.25 s collar) / utterance accuracy % / speakers found. sherpa \"hint\" is the count given; sherpa \"none\" is threshold 0.8. Gladia \"hint\" is `number_of_speakers`; ElevenLabs \"hint\" is `num_speakers` (a maximum).").AppendLine()
        .AppendLine("| Meeting | True | " + string.Join(" | ", columns.Select(c => $"{c.Service} {c.System}")) + " |")
        .AppendLine("|---|---:|" + string.Concat(columns.Select(_ => "---:|")));
    foreach (var meeting in meetings)
        md.AppendLine($"| {meeting.Id} | {meeting.Speakers} | " + string.Join(" | ", columns.Select(c =>
            all.FirstOrDefault(r => r.Meeting == meeting.Id && r.Model == c.Service && r.System == c.System) is { } r
                ? $"{r.Der * 100:F1} / {r.Accuracy * 100:F1} / {r.Estimated}" : "–")) + " |");
    foreach (var (label, scripted) in new[] { ("Mean, scripted", true), ("Mean, synthetic", false) })
        md.AppendLine($"| {label} | | " + string.Join(" | ", columns.Select(c =>
            all.Where(r => r.Meeting.StartsWith("negotiation", StringComparison.Ordinal) == scripted && r.Model == c.Service && r.System == c.System).ToList() is { Count: > 0 } rs
                ? $"{rs.Average(r => r.Der) * 100:F1} / {rs.Sum(r => r.Correct) * 100.0 / rs.Sum(r => r.Total):F1} / {rs.Count(r => r.Estimated == r.Speakers)} of {rs.Count} exact" : "–")) + " |");
    md.AppendLine().AppendLine("| Service | Audio sent, min |").AppendLine("|---|---:|");
    foreach (var (service, s) in sent)
        md.AppendLine($"| {service} | {s / 60:F1} |");
    md.AppendLine().AppendJoin(Environment.NewLine, realLines).AppendLine().AppendLine()
        .AppendLine("Deletions:").AppendLine().AppendJoin(Environment.NewLine, Cloud.Deletions.Select(d => "- " + d)).AppendLine().AppendLine()
        .AppendLine("Notes:").AppendLine().AppendJoin(Environment.NewLine, cloudNotes.Select(n => "- " + n)).AppendLine().AppendLine()
        .AppendJoin(Environment.NewLine, cloudDetails.Order());
    Directory.CreateDirectory(outDir);
    var path = Path.Combine(outDir, $"kanal-diarization-cloud-{DateTime.Now:yyyyMMdd-HHmm}");
    File.WriteAllText(path + ".md", md.ToString());
    File.WriteAllText(path + ".json", JsonSerializer.Serialize(new Run(DateTime.Now, machine, Engines.DefaultThreshold, Corpus.RoomDescription, cloudResults, [], cloudNotes, cloudDetails), json));
    Console.WriteLine(md);
    return 0;
}

int SherpaRttm(string name)
{
    var embedding = Engines.ModelPath(modelsDir, data, DiarizationModelCatalog.DefaultEmbedding);
    var rtf = new List<double>();
    Directory.CreateDirectory(Path.Combine(data, "cloud"));
    foreach (var meeting in storedIds.Select(Stored))
    foreach (var hint in new int?[] { meeting.Speakers, null })
    {
        using var diarization = Engines.Load(segmentation, embedding, hint, hint is null ? 0.8f : null);
        var (turns, seconds) = Clock.Time(() => Engines.Diarize(diarization, meeting.Samples));
        rtf.Add(seconds / meeting.Seconds);
        File.WriteAllLines(Path.Combine(data, "cloud", $"{name}-{meeting.Id}-{(hint is null ? "none" : $"hint{hint}")}.rttm"), turns.Select(t =>
            $"SPEAKER {meeting.Id} 1 {t.StartSeconds:F3} {t.EndSeconds - t.StartSeconds:F3} <NA> <NA> {t.Speaker} <NA> <NA>"));
        Console.WriteLine($"{meeting.Id,-24} hint {hint?.ToString() ?? "none",-4} {turns.Select(t => t.Speaker).Distinct().Count()} speakers, RTF {seconds / meeting.Seconds:F3}");
    }

    Console.WriteLine($"{segmentation}: RTF mean {rtf.Average():F3}, {machine}");
    return 0;
}

Meeting Stored(string id)
{
    var wav = Path.Combine(data, "meetings", id + ".wav");
    using var file = File.OpenRead(wav);
    var samples = PcmConvert.BytesToShorts(WavFile.Read(file).Pcm16).Select(s => s / 32768f).ToArray();
    var truth = File.ReadAllLines(Path.ChangeExtension(wav, ".rttm")).Select(l => l.Split(' ')).Select(f =>
    {
        var start = double.Parse(f[3]);
        return new Segment(start, start + double.Parse(f[4]), f[7]);
    }).ToList();
    var scripted = id.StartsWith("negotiation", StringComparison.Ordinal);
    return new Meeting(id, scripted ? "mix" : id.Split('-')[0], truth.Select(t => t.Speaker).Distinct().Count(),
        scripted || id.Contains("overlap", StringComparison.Ordinal), id.EndsWith("room", StringComparison.Ordinal), samples, truth);
}

static string Sample(string lang) => lang switch
{
    "zh" => "我们先确认一下支架KB-4471的最新图纸版本。",
    "de" => "Wir sollten zuerst die aktuelle Zeichnungsversion für den Halter KB-4471 bestätigen.",
    _ => "Najpierw potwierdźmy aktualną wersję rysunku wspornika KB-4471.",
};

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
internal sealed record Result(
    string Meeting, string Lang, int Speakers, bool Overlap, bool Room, string Model, string System, double Threshold,
    double Der, double Missed, double FalseAlarm, double Confusion, double DerNoCollar,
    int Estimated, int Tags, int Correct, int Total, int ShortCorrect, int ShortTotal, int LongCorrect, int LongTotal, double Rtf)
{
    public double Accuracy => Total == 0 ? 0 : Correct / (double)Total;
}

[ExcludeFromCodeCoverage]
internal sealed record Cost(string Model, string Meeting, double AudioSeconds, double LoadSeconds, double Seconds, double PeakDeltaMb, double PeakMb);

[ExcludeFromCodeCoverage]
internal sealed record Run(DateTime Stamp, string Machine, float DefaultThreshold, string Room, List<Result> Results, List<Cost> Costs, List<string> Notes, List<string>? Details = null);

[ExcludeFromCodeCoverage]
internal partial class Program;
