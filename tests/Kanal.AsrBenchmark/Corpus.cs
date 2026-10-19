using System.ComponentModel;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Formats.Tar;
using System.IO.Compression;
using System.Globalization;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Kanal.Audio;
using SherpaOnnx;

namespace Kanal.AsrBenchmark;

[ExcludeFromCodeCoverage]
internal sealed record Clip(string Set, string Lang, string Id, string Reference, short[] Samples)
{
    public double Seconds => Samples.Length / (double)Corpus.Rate;
}

[ExcludeFromCodeCoverage]
internal sealed record AscendClip(string Id, string Language, double Seconds, string Text, long Session, long Speaker, string Src);

[ExcludeFromCodeCoverage]
internal static class Corpus
{
    public const int Rate = 16_000;

    private const double MinClipSeconds = 3;

    private const double MaxClipSeconds = 20;

    public const string AscendSet = "ascend";

    public static readonly string[] Languages = ["zh", "de", "pl", "en"];

    // FLEURS (Conneau et al. 2022), CC-BY-4.0. Each pinned revision keeps the clip choice the same across machines.
    private const string FleursRevision = "70bb2e84b976b7e960aa89f1c648e09c59f894dd";

    private static readonly Dictionary<string, string> FleursConfig = new()
    {
        ["zh"] = "cmn_hans_cn",
        ["de"] = "de_de",
        ["pl"] = "pl_pl",
        ["en"] = "en_us",
    };

    private static readonly Dictionary<string, string> PiperVoice = new()
    {
        ["zh"] = "vits-piper-zh_CN-huayan-medium",
        ["de"] = "vits-piper-de_DE-thorsten-medium-int8",
        ["pl"] = "vits-piper-pl_PL-gosia-medium-int8",
        ["en"] = "vits-piper-en_US-lessac-medium-int8",
    };

    public static IReadOnlyList<(string Lang, string Text)> Meeting { get; } =
    [
        ("zh", "支架 KB-4471 的焊缝在 2000 次循环后出现裂纹，我们需要重新做疲劳测试。"),
        ("zh", "下一批货预计 3 月 15 日从宁波发出，大约需要五周到汉堡。"),
        ("zh", "我们可以把壁厚从 2.5 毫米减到 2 毫米，但单价只能降低百分之三。"),
        ("zh", "请把材料从 S235JR 换成 X5CrNi18-10，客户要求防护等级达到 IP67。"),
        ("zh", "M10 螺栓的孔位超出公差 0.3 毫米。"),
        ("zh", "您能在周五之前给我们发一份 5000 件的报价吗？"),
        ("zh", "根据检测报告 PB-2291，涂层在盐雾试验中只达到 480 小时。"),
        ("zh", "在批准模具之前，我们需要 C 版图纸。"),
        ("zh", "自周二以来，格利维采装配线上一直缺少 KB-4471 支架。"),
        ("zh", "我们按图纸将螺栓拧紧力矩设定为 25 Nm。"),
        ("zh", "供应商能否把样品发货提前一周？"),
        ("zh", "客户拒收了 L-2418 批次，因为 O 形圈和槽不匹配。"),
        ("de", "Die Schweißnaht am Halter KB-4471 zeigt nach 2000 Zyklen Risse, wir müssen den Ermüdungstest wiederholen."),
        ("de", "Die nächste Lieferung soll am 15. März in Ningbo verschifft werden und braucht etwa fünf Wochen bis Hamburg."),
        ("de", "Wir können die Wandstärke von 2,5 mm auf 2 mm reduzieren, aber der Stückpreis sinkt nur um drei Prozent."),
        ("de", "Bitte stellen Sie das Material von S235JR auf X5CrNi18-10 um, der Kunde verlangt Schutzart IP67."),
        ("de", "Die Bohrung für die Schraube M10 liegt 0,3 mm außerhalb der Toleranz."),
        ("de", "Können Sie uns bis Freitag ein Angebot für 5000 Stück schicken?"),
        ("de", "Laut Prüfbericht PB-2291 erreicht die Beschichtung nur 480 Stunden im Salzsprühtest."),
        ("de", "Wir brauchen die Zeichnung in Revision C, bevor wir das Werkzeug freigeben."),
        ("de", "Seit Dienstag fehlen an der Montagelinie in Gliwice die Halter KB-4471."),
        ("de", "Wir haben das Anzugsmoment der Schrauben laut Zeichnung auf 25 Nm eingestellt."),
        ("de", "Kann der Lieferant den Versand der Muster um eine Woche vorziehen?"),
        ("de", "Der Kunde hat die Charge L-2418 abgelehnt, weil die O-Ringe nicht in die Nut passen."),
        ("pl", "W spoinie wspornika KB-4471 po 2000 cyklach pojawiły się pęknięcia, musimy powtórzyć test zmęczeniowy."),
        ("pl", "Następna dostawa ma wyjść z Ningbo 15 marca i dotrze do Hamburga po około pięciu tygodniach."),
        ("pl", "Możemy zmniejszyć grubość ścianki z 2,5 mm do 2 mm, ale cena jednostkowa spadnie tylko o trzy procent."),
        ("pl", "Proszę zmienić materiał z S235JR na X5CrNi18-10, klient wymaga stopnia ochrony IP67."),
        ("pl", "Otwór pod śrubę M10 jest 0,3 mm poza tolerancją."),
        ("pl", "Czy mogą nam Państwo przesłać ofertę na 5000 sztuk do piątku?"),
        ("pl", "Według raportu z badań PB-2291 powłoka wytrzymuje w teście mgły solnej tylko 480 godzin."),
        ("pl", "Potrzebujemy rysunku w rewizji C, zanim zatwierdzimy narzędzie."),
        ("pl", "Na linii montażowej w Gliwicach brakuje wsporników KB-4471 od wtorku."),
        ("pl", "Moment dokręcania śrub ustawiliśmy na 25 Nm, zgodnie z rysunkiem."),
        ("pl", "Czy dostawca może przyspieszyć wysyłkę próbek o tydzień?"),
        ("pl", "Klient odrzucił partię L-2418, bo uszczelki O-ring nie pasują do rowka."),
        ("en", "The weld seam on bracket KB-4471 cracked after 2000 cycles, so we need to repeat the fatigue test."),
        ("en", "The next shipment is expected to leave Ningbo on March 15 and will take about five weeks to reach Hamburg."),
        ("en", "We can reduce the wall thickness from 2.5 mm to 2 mm, but the unit price can only drop by three percent."),
        ("en", "Please change the material from S235JR to X5CrNi18-10. The customer requires protection class IP67."),
        ("en", "The hole for the M10 screw is 0.3 mm out of tolerance."),
        ("en", "Can you send us a quote for 5000 pieces by Friday?"),
        ("en", "According to test report PB-2291, the coating only reaches 480 hours in the salt spray test."),
        ("en", "We need the drawing in revision C before we release the tool."),
        ("en", "The assembly line in Gliwice has been short of KB-4471 brackets since Tuesday."),
        ("en", "We set the tightening torque of the screws to 25 Nm, as per the drawing."),
        ("en", "Can the supplier bring the sample shipment forward by one week?"),
        ("en", "The customer rejected batch L-2418 because the O-ring seals do not fit the groove."),
    ];

    public static IReadOnlyList<Clip> Load(string data, IReadOnlyCollection<string>? sets = null)
    {
        var manifest = Path.Combine(data, "clips.tsv");
        if (!File.Exists(manifest))
            return [];
        return File.ReadAllLines(manifest)
            .Select(line => line.Split('\t'))
            .Where(f => sets is null || sets.Contains(f[0]))
            .Select(f => new Clip(f[0], f[1], f[2], f[4], Read(Path.Combine(data, f[3]))))
            .ToList();
    }

    public static readonly Dictionary<string, Func<HttpClient, string, int, Task<List<string>>>> Sets = new()
    {
        ["fleurs"] = FetchFleursAsync,
        ["meeting-piper"] = (http, data, _) => SynthesisePiperAsync(http, data),
        ["meeting-11labs"] = (http, data, _) => SynthesiseElevenLabsAsync(http, data),
        ["voxpopuli"] = FetchVoxPopuliAsync,
        ["ami"] = FetchAmiAsync,
        ["aishell4"] = FetchAishell4Async,
        ["commonvoice"] = FetchCommonVoiceAsync,
        [AscendSet] = FetchAscendAsync,
    };

    public static readonly string[] OptInSets = ["meeting-11labs", "commonvoice"];

    public static IEnumerable<string> DefaultSets => Sets.Keys.Except(OptInSets);

    public static async Task<int> FetchAsync(string data, int perLanguage, IReadOnlyCollection<string> sets)
    {
        var failed = 0;
        Directory.CreateDirectory(data);
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
        var manifest = Path.Combine(data, "clips.tsv");
        var old = File.Exists(manifest) ? File.ReadAllLines(manifest).ToList() : [];
        var rows = KeepOtherSets(old, sets);
        foreach (var set in sets)
        {
            try
            {
                rows.AddRange(await Sets[set](http, data, perLanguage));
            }
            catch (Exception e) when (e is HttpRequestException or InvalidOperationException or InvalidDataException or IOException or TaskCanceledException)
            {
                failed++;
                Console.Error.WriteLine($"{set}: {e.Message} The old clips of this set stay.");
                rows.AddRange(old.Where(l => SetOf(l) == set));
            }
        }

        await File.WriteAllLinesAsync(manifest, rows);
        Console.WriteLine($"{rows.Count} clips listed in {manifest}");
        return failed;
    }

    internal static List<string> KeepOtherSets(IEnumerable<string> old, IReadOnlyCollection<string> fetched) =>
        old.Where(l => Sets.ContainsKey(SetOf(l)) && !fetched.Contains(SetOf(l))).ToList();

    private static string SetOf(string row) => row.Split('\t')[0];

    private static async Task<List<string>> FetchFleursAsync(HttpClient http, string data, int count)
    {
        var rows = new List<string>();
        foreach (var lang in Languages)
        {
            var root = $"https://huggingface.co/datasets/google/fleurs/resolve/{FleursRevision}/data/{FleursConfig[lang]}";
            var transcripts = (await http.GetStringAsync($"{root}/test.tsv"))
                .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(l => l.Split('\t'))
                .GroupBy(f => f[1])
                .ToDictionary(g => g.Key, g => g.First()[2]);
            rows.AddRange(await StreamTarAsync(http, $"{root}/audio/test.tar.gz", "fleurs", lang, data, count, transcripts, limitLength: false));
        }

        return rows;
    }

    // VoxPopuli (Wang et al. 2021), CC0; raw audio © European Union, source: European Parliament.
    // This revision still holds the per-language tar.gz files; the next one moved to Parquet.
    private const string VoxPopuliRevision = "719aaef8225945c0d80b277de6c79aa42ab053d5";

    private static async Task<List<string>> FetchVoxPopuliAsync(HttpClient http, string data, int count)
    {
        var rows = new List<string>();
        foreach (var lang in new[] { "de", "pl", "en" })
        {
            var root = $"https://huggingface.co/datasets/facebook/voxpopuli/resolve/{VoxPopuliRevision}/data/{lang}";
            var transcripts = (await http.GetStringAsync($"{root}/asr_test.tsv"))
                .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Skip(1)
                .Select(l => l.Split('\t'))
                .ToDictionary(f => f[0] + ".wav", f => f[1]);
            rows.AddRange(await StreamTarAsync(http, $"{root}/test/test_part_0.tar.gz", "voxpopuli", lang, data, count, transcripts, limitLength: true));
        }

        return rows;
    }

    // A test archive is about 0.5 GB; reading it as a stream stops after the first clips.
    private static async Task<List<string>> StreamTarAsync(HttpClient http, string url, string set, string lang, string data,
        int count, Dictionary<string, string> transcripts, bool limitLength)
    {
        var folder = Path.Combine(data, set, lang);
        Directory.CreateDirectory(folder);
        var rows = new List<string>();
        using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();
        await using var gzip = new GZipStream(await response.Content.ReadAsStreamAsync(), CompressionMode.Decompress);
        using var tar = new TarReader(gzip);
        while (rows.Count < count && await tar.GetNextEntryAsync() is { } entry)
        {
            var name = Path.GetFileName(entry.Name);
            if (entry.DataStream is null || !name.EndsWith(".wav") || !transcripts.TryGetValue(name, out var text) || text.Length == 0)
                continue;
            name = name.Replace(':', '-');
            var path = Path.Combine(folder, name);
            await using (var file = File.Create(path))
                await entry.DataStream.CopyToAsync(file);
            var seconds = Read(path).Length / (double)Rate;
            if (limitLength && seconds is < MinClipSeconds or > MaxClipSeconds)
            {
                File.Delete(path);
                continue;
            }

            rows.Add(string.Join('\t', set, lang, Path.GetFileNameWithoutExtension(name), $"{set}/{lang}/{name}", text));
        }

        Console.WriteLine($"{set} {lang}: {rows.Count} clips");
        return rows;
    }

    private static async Task<List<string>> SynthesisePiperAsync(HttpClient http, string data)
    {
        var rows = new List<string>();
        var folder = Path.Combine(data, "meeting-piper");
        Directory.CreateDirectory(folder);
        foreach (var (lang, voice) in PiperVoice)
        {
            var dir = Path.Combine(data, "tts", voice);
            if (!Directory.Exists(dir))
                await ExtractAsync(http, $"https://github.com/k2-fsa/sherpa-onnx/releases/download/tts-models/{voice}.tar.bz2",
                    Path.Combine(data, "tts"));

            var config = new OfflineTtsConfig();
            config.Model.Vits.Model = Directory.GetFiles(dir, "*.onnx").Single();
            config.Model.Vits.Tokens = Path.Combine(dir, "tokens.txt");
            config.Model.Vits.DataDir = Path.Combine(dir, "espeak-ng-data");
            config.Model.NumThreads = 4;
            using var tts = new OfflineTts(config);
            var index = 0;
            foreach (var (_, text) in Meeting.Where(m => m.Lang == lang))
            {
                var audio = tts.Generate(text, 1.0f, 0);
                var pcm = audio.Samples.Select(s => (short)Math.Clamp(s * 32767f, short.MinValue, short.MaxValue)).ToArray();
                var resampler = new LinearResampler(audio.SampleRate, Rate);
                var buffer = new short[resampler.GetMaxOutputCount(pcm.Length)];
                var id = $"{lang}-{++index:D2}";
                await WritePaddedAsync(Path.Combine(folder, id + ".wav"), buffer[..resampler.Resample(pcm, buffer)]);
                rows.Add(string.Join('\t', "meeting-piper", lang, id, $"meeting-piper/{id}.wav", text));
            }

            Console.WriteLine($"meeting-piper {lang}: {index} clips synthesised with {voice}");
        }

        return rows;
    }

    // ElevenLabs premade voices. A clip on disk is never requested again: each request costs characters.
    private static readonly (string Name, string Id)[] ElevenLabsVoices =
        [("alice", "Xb7hH8MSUJpSbSDYk0k2"), ("george", "JBFqnCBsd6RMkjVDRZzb"), ("lily", "pFZP5JQG7iQjIQuC4Bku")];

    private const string ElevenLabsModel = "eleven_multilingual_v2";

    private static async Task<List<string>> SynthesiseElevenLabsAsync(HttpClient http, string data)
    {
        var rows = new List<string>();
        var folder = Path.Combine(data, "meeting-11labs");
        Directory.CreateDirectory(folder);
        string? key = null;
        var requested = 0;
        foreach (var lang in Languages)
        {
            var index = 0;
            foreach (var (_, text) in Meeting.Where(m => m.Lang == lang))
            {
                index++;
                foreach (var (voice, voiceId) in ElevenLabsVoices)
                {
                    var id = $"{lang}-{index:D2}-{voice}";
                    var path = Path.Combine(folder, id + ".wav");
                    if (!File.Exists(path))
                    {
                        key ??= Engine.Key("ELEVENLABS_API_KEY");
                        using var request = new HttpRequestMessage(HttpMethod.Post,
                            $"https://api.elevenlabs.io/v1/text-to-speech/{voiceId}?output_format=pcm_16000")
                        {
                            Content = JsonContent.Create(new { text, model_id = ElevenLabsModel }),
                        };
                        request.Headers.Add("xi-api-key", key);
                        using var response = await http.SendAsync(request);
                        if (!response.IsSuccessStatusCode)
                            throw new InvalidOperationException($"ElevenLabs refused {id} ({(int)response.StatusCode}): {await response.Content.ReadAsStringAsync()}");
                        await WritePaddedAsync(path, PcmConvert.BytesToShorts(await response.Content.ReadAsByteArrayAsync()));
                        requested += text.Length;
                    }

                    rows.Add(string.Join('\t', "meeting-11labs", lang, id, $"meeting-11labs/{id}.wav", text));
                }
            }
        }

        Console.WriteLine($"meeting-11labs: {rows.Count} clips, {requested} characters sent to ElevenLabs");
        return rows;
    }

    private static async Task WritePaddedAsync(string path, short[] speech)
    {
        short[] padded = [.. new short[Rate * 3 / 10], .. speech, .. new short[Rate / 2]];
        await using var file = File.Create(path);
        WavFile.Write(file, PcmConvert.ShortsToBytes(padded), Rate, 1);
    }

    // AMI Meeting Corpus (Carletta et al. 2005), CC-BY-4.0, through edinburghcstr/ami. SDM: the single far-field array microphone.
    private const string AmiRevision = "46f28f2503e2ec48f8867a84eef356c70476beab";

    private const string AmiMeeting = "EN2002a";

    private static async Task<List<string>> FetchAmiAsync(HttpClient http, string data, int count)
    {
        var segments = new List<(string Id, double Begin, double End, string Text, string Src)>();
        for (var offset = 0; ; offset += 100)
        {
            using var page = JsonDocument.Parse(await http.GetStringAsync(
                $"https://datasets-server.huggingface.co/rows?dataset=edinburghcstr/ami&config=sdm&split=test&offset={offset}&length=100"));
            var rows = page.RootElement.GetProperty("rows").EnumerateArray().Select(r => r.GetProperty("row")).ToList();
            var mine = rows.Where(r => r.GetProperty("meeting_id").GetString() == AmiMeeting).ToList();
            segments.AddRange(mine.Select(r => (r.GetProperty("audio_id").GetString()!, r.GetProperty("begin_time").GetDouble(),
                r.GetProperty("end_time").GetDouble(), r.GetProperty("text").GetString()!,
                r.GetProperty("audio")[0].GetProperty("src").GetString()!)));
            if (mine.Count < rows.Count || rows.Count < 100)
                break;
        }

        if (segments.Count == 0)
            throw new InvalidDataException($"AMI: no row of {AmiMeeting} on the pages read. The order of the dataset may have changed.");
        if (!segments[0].Src.Contains(AmiRevision))
            throw new InvalidDataException($"AMI: the dataset server no longer serves revision {AmiRevision}.");

        var picked = PickAmi(segments, count);
        var folder = Path.Combine(data, "ami", "en");
        Directory.CreateDirectory(folder);
        var result = new List<string>();
        foreach (var s in picked)
        {
            await File.WriteAllBytesAsync(Path.Combine(folder, s.Id + ".wav"), await http.GetByteArrayAsync(s.Src));
            result.Add(string.Join('\t', "ami", "en", s.Id, $"ami/en/{s.Id}.wav", s.Text));
        }

        Console.WriteLine($"ami en: {result.Count} clips from {AmiMeeting} ({segments.Count} segments, {picked.Count} without overlap)");
        return result;
    }

    internal static List<(string Id, double Begin, double End, string Text, string Src)> PickAmi(
        IReadOnlyList<(string Id, double Begin, double End, string Text, string Src)> segments, int count) =>
        segments
            .Where(s => s.End - s.Begin is >= MinClipSeconds and <= MaxClipSeconds)
            .Where(s => !segments.Any(o => o.Id != s.Id && o.Begin < s.End && s.Begin < o.End))
            .OrderBy(s => s.Begin)
            .Take(count)
            .ToList();

    // AISHELL-4 (Fu et al. 2021), CC-BY-SA-4.0 (openslr.org/111). Channel 0 of the 8-channel far-field array.
    private const string Aishell4Revision = "aada72727856313b19d4a030383c426364931dbf";

    private static readonly string[] Aishell4Sessions = ["L_R003S01C02", "L_R004S01C01"];

    private static async Task<List<string>> FetchAishell4Async(HttpClient http, string data, int count)
    {
        var root = $"https://huggingface.co/datasets/AISHELL/AISHELL-4/resolve/{Aishell4Revision}/test";
        var folder = Path.Combine(data, "aishell4", "zh");
        Directory.CreateDirectory(folder);
        var rows = new List<string>();
        foreach (var session in Aishell4Sessions)
        {
            var intervals = TextGrid(await http.GetStringAsync($"{root}/TextGrid/{session}.TextGrid"));
            var picked = PickAishell4(intervals, (count + Aishell4Sessions.Length - 1) / Aishell4Sessions.Length);
            if (picked.Count == 0)
                throw new InvalidDataException($"AISHELL-4 {session}: no interval passes the length and overlap filters.");

            // ponytail: 160 kB per second of 8-channel FLAC is above the rate of the test sessions; the length check below catches a shortfall.
            var bytes = (long)((picked[^1].End + 10) * 160_000);
            var part = Path.Combine(folder, session + ".part.flac");
            using (var request = new HttpRequestMessage(HttpMethod.Get, $"{root}/wav/{session}.flac"))
            {
                request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(0, bytes);
                using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
                response.EnsureSuccessStatusCode();
                await using var file = File.Create(part);
                await response.Content.CopyToAsync(file);
            }

            var wav = Path.Combine(folder, session + ".wav");
            var (exit, error) = await FfmpegAsync("-i", part, "-af", "pan=mono|c0=c0", "-ar", "16000", "-c:a", "pcm_s16le", wav);
            File.Delete(part);
            var audio = File.Exists(wav) ? Read(wav) : [];
            File.Delete(wav);
            if (audio.Length < picked[^1].End * Rate)
                throw new InvalidDataException($"AISHELL-4 {session}: the partial download decoded to {audio.Length / Rate} s only. ffmpeg exit code {exit}: {error}");

            foreach (var i in picked)
            {
                var id = $"{session}-{i.Begin * 1000:F0}";
                await using (var file = File.Create(Path.Combine(folder, id + ".wav")))
                    WavFile.Write(file, PcmConvert.ShortsToBytes(audio.AsSpan((int)(i.Begin * Rate), (int)((i.End - i.Begin) * Rate))), Rate, 1);
                rows.Add(string.Join('\t', "aishell4", "zh", id, $"aishell4/zh/{id}.wav", Clean(i.Text)));
            }
        }

        Console.WriteLine($"aishell4 zh: {rows.Count} clips from {string.Join(", ", Aishell4Sessions)}");
        return rows;
    }

    // Common Voice Scripted Speech 27.0 (Ardila et al. 2020), CC0-1.0, from Mozilla Data Collective. The dataset IDs pin the version.
    private static readonly Dictionary<string, string> CommonVoiceDataset = new()
    {
        ["zh"] = "cmu5lcunh00qimh078sdd5so8",
        ["de"] = "cmu5ktqhe00pgmh07p1xamabd",
        ["pl"] = "cmu5wsxwp00e0nq07m8v0274j",
        ["en"] = "cmu5jplf300nwmh07iqvk9leo",
    };

    private const long CommonVoiceCapBytes = 2L << 30;

    // The archive lists the TSV files before the clips, so reading the start of the stream is enough.
    private static async Task<List<string>> FetchCommonVoiceAsync(HttpClient http, string data, int count)
    {
        var key = Engine.Key("MDC_API_KEY");
        var rows = new List<string>();
        foreach (var (lang, dataset) in CommonVoiceDataset)
        {
            using var session = new HttpRequestMessage(HttpMethod.Post, $"https://mozilladatacollective.com/api/datasets/{dataset}/download");
            session.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", key);
            using var plan = await http.SendAsync(session);
            if (!plan.IsSuccessStatusCode)
                throw new InvalidOperationException($"Common Voice {lang}: {(int)plan.StatusCode} {await plan.Content.ReadAsStringAsync()}");
            using var body = JsonDocument.Parse(await plan.Content.ReadAsStringAsync());
            var url = body.RootElement.GetProperty("downloadUrl").GetString()!;

            var folder = Path.Combine(data, "commonvoice", lang);
            Directory.CreateDirectory(folder);
            Dictionary<string, string>? test = null;
            var found = 0;
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(0, CommonVoiceCapBytes);
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
            response.EnsureSuccessStatusCode();
            await using var gzip = new GZipStream(await response.Content.ReadAsStreamAsync(), CompressionMode.Decompress);
            using var tar = new TarReader(gzip);
            try
            {
                while (found < count && await tar.GetNextEntryAsync() is { } entry)
                {
                    if (entry.DataStream is null)
                        continue;
                    if (entry.Name.EndsWith("/test.tsv"))
                    {
                        using var reader = new StreamReader(entry.DataStream);
                        var lines = (await reader.ReadToEndAsync()).Split('\n', StringSplitOptions.RemoveEmptyEntries);
                        var header = lines[0].Split('\t');
                        int path = Array.IndexOf(header, "path"), sentence = Array.IndexOf(header, "sentence");
                        test = lines.Skip(1).Select(l => l.Split('\t')).ToDictionary(f => f[path], f => f[sentence]);
                        continue;
                    }

                    var name = Path.GetFileName(entry.Name);
                    if (test is null || !test.TryGetValue(name, out var text))
                        continue;
                    var mp3 = Path.Combine(folder, name);
                    await using (var file = File.Create(mp3))
                        await entry.DataStream.CopyToAsync(file);
                    var id = Path.GetFileNameWithoutExtension(name);
                    var wav = Path.Combine(folder, id + ".wav");
                    var (exit, error) = await FfmpegAsync("-i", mp3, "-ac", "1", "-ar", "16000", "-c:a", "pcm_s16le", wav);
                    File.Delete(mp3);
                    if (!File.Exists(wav))
                        throw new InvalidOperationException($"Common Voice {lang}: ffmpeg could not decode {name}. Exit code {exit}: {error}");
                    rows.Add(string.Join('\t', "commonvoice", lang, id, $"commonvoice/{lang}/{id}.wav", text));
                    found++;
                }
            }
            catch (Exception e) when (e is InvalidDataException or EndOfStreamException or IOException && found < count)
            {
                throw new InvalidDataException($"Common Voice {lang}: {found} test clips in the first {CommonVoiceCapBytes >> 30} GB of the archive ({e.Message}).");
            }

            if (found < count)
                throw new InvalidDataException($"Common Voice {lang}: {found} test clips in the first {CommonVoiceCapBytes >> 30} GB of the archive.");
            Console.WriteLine($"commonvoice {lang}: {found} clips");
        }

        return rows;
    }

    internal static string RoomLanguage(string lang) => lang == "mixed" ? "zh" : lang;

    internal static List<AscendClip> PickAscend(
        IReadOnlyList<AscendClip> rows,
        string language, int count)
    {
        var groups = rows
            .Where(r => r.Language == language && r.Seconds is >= MinClipSeconds and <= MaxClipSeconds)
            .Where(r => !r.Text.Contains("[UNK]") && Clean(r.Text).Length > 0)
            .GroupBy(r => (r.Session, r.Speaker))
            .OrderBy(g => g.Key)
            .Select(g => g.OrderBy(r => r.Id, StringComparer.Ordinal).ToList())
            .ToList();
        var quota = new int[groups.Count];
        for (var left = count; left > 0 && groups.Where((g, i) => quota[i] < g.Count).Any();)
            for (var i = 0; i < groups.Count && left > 0; i++)
                if (quota[i] < groups[i].Count)
                {
                    quota[i]++;
                    left--;
                }

        return groups
            .SelectMany((g, i) => Enumerable.Range(0, quota[i]).Select(k => g[k * g.Count / quota[i]]))
            .OrderBy(r => r.Id, StringComparer.Ordinal)
            .ToList();
    }

    // ASCEND (Lovenia et al. 2022), CC-BY-SA-4.0, through CAiRE/ASCEND.
    private const string AscendRevision = "737e9800ae31be9932ba8464c80366559bd28424";

    internal static readonly (string Language, int Count)[] AscendGroups = [("mixed", 60), ("zh", 15), ("en", 15)];

    private static async Task<List<string>> FetchAscendAsync(HttpClient http, string data, int count)
    {
        var all = new List<AscendClip>();
        var total = 1;
        for (var offset = 0; offset < total; offset += 100)
        {
            using var page = JsonDocument.Parse(await WithRetryAsync(() => GetRowsPageAsync(http,
                $"https://datasets-server.huggingface.co/rows?dataset=CAiRE/ASCEND&config=main&split=test&offset={offset}&length=100")));
            total = page.RootElement.GetProperty("num_rows_total").GetInt32();
            var rows = page.RootElement.GetProperty("rows").EnumerateArray().Select(r => r.GetProperty("row")).ToList();
            all.AddRange(rows.Select(r => new AscendClip(r.GetProperty("id").GetString()!, r.GetProperty("language").GetString()!,
                r.GetProperty("duration").GetDouble(), r.GetProperty("transcription").GetString()!,
                r.GetProperty("session_id").GetInt64(), r.GetProperty("original_speaker_id").GetInt64(),
                r.GetProperty("audio")[0].GetProperty("src").GetString()!)));
        }

        if (all.Count == 0 || !all[0].Src.Contains(AscendRevision))
            throw new InvalidDataException($"ASCEND: the dataset server no longer serves revision {AscendRevision}.");
        if (total != AscendTestRows || all.Count != AscendTestRows)
            throw new InvalidDataException($"ASCEND: the test split has {total} rows and {all.Count} were read. Expected {AscendTestRows}.");

        var result = new List<string>();
        foreach (var (language, quota) in AscendGroups)
        {
            var picked = PickAscend(all, language, quota);
            var folder = Path.Combine(data, AscendSet, language);
            Directory.CreateDirectory(folder);
            foreach (var r in picked)
            {
                var id = $"s{r.Session}-p{r.Speaker}-{r.Id}";
                await File.WriteAllBytesAsync(Path.Combine(folder, id + ".wav"), await WithRetryAsync(() => http.GetByteArrayAsync(r.Src)));
                result.Add(string.Join('\t', AscendSet, language, id, $"{AscendSet}/{language}/{id}.wav", Regex.Replace(r.Text, @"\s+", " ").Trim()));
            }

            Console.WriteLine($"{AscendSet} {language}: {picked.Count} clips from {picked.Select(r => (r.Session, r.Speaker)).Distinct().Count()} speaker and session groups");
        }

        return result;
    }

    private const int AscendTestRows = 1315;

    private static async Task<string> GetRowsPageAsync(HttpClient http, string url)
    {
        using var response = await http.GetAsync(url);
        if ((int)response.StatusCode == 429 && response.Headers.RetryAfter?.Delta is { } wait)
            await Task.Delay(wait);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync();
    }

    internal static async Task<T> WithRetryAsync<T>(Func<Task<T>> action, TimeSpan? delay = null)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await action();
            }
            catch (Exception e) when (attempt < 4 && e is HttpRequestException or TaskCanceledException)
            {
                await Task.Delay((delay ?? TimeSpan.FromSeconds(3)) * attempt);
            }
        }
    }

    internal static List<(int Tier, double Begin, double End, string Text)> PickAishell4(
        IReadOnlyList<(int Tier, double Begin, double End, string Text)> intervals, int count) =>
        intervals
            .Where(i => Clean(i.Text).Length > 0 && i.End - i.Begin is >= MinClipSeconds and <= MaxClipSeconds)
            .Where(i => !intervals.Any(o => o.Tier != i.Tier && o.Text.Trim().Length > 0 && o.Begin < i.End && i.Begin < o.End))
            .OrderBy(i => i.Begin)
            .Take(count)
            .ToList();

    private static async Task<(int ExitCode, string Error)> FfmpegAsync(params string[] args)
    {
        string[] all = ["-v", "error", "-y", .. args];
        Process process;
        try
        {
            process = Process.Start(new ProcessStartInfo("ffmpeg", all) { RedirectStandardError = true })!;
        }
        catch (Win32Exception)
        {
            throw new InvalidOperationException("ffmpeg is not on PATH. Install ffmpeg and run fetch again.");
        }

        using (process)
        {
            var error = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            return (process.ExitCode, (await error).Trim());
        }
    }

    internal static string Clean(string text) => Regex.Replace(text, "<[^>]*>", "").Trim();

    internal static List<(int Tier, double Begin, double End, string Text)> TextGrid(string grid)
    {
        var result = new List<(int, double, double, string)>();
        var tier = 0;
        double begin = 0, end = 0;
        foreach (var line in grid.Split('\n').Select(l => l.Trim()))
        {
            if (line.StartsWith("item ["))
                tier++;
            else if (line.StartsWith("xmin = "))
                begin = double.Parse(line[7..], CultureInfo.InvariantCulture);
            else if (line.StartsWith("xmax = "))
                end = double.Parse(line[7..], CultureInfo.InvariantCulture);
            else if (line.StartsWith("text = \""))
                result.Add((tier, begin, end, line[8..line.LastIndexOf('"')]));
        }

        return result;
    }

    private static async Task ExtractAsync(HttpClient http, string url, string into)
    {
        Directory.CreateDirectory(into);
        var archive = Path.Combine(into, Path.GetFileName(url));
        await using (var file = File.Create(archive))
        await using (var body = await http.GetStreamAsync(url))
            await body.CopyToAsync(file);
        // bsdtar ships with Windows 10+ and macOS and reads bzip2; .NET has no bzip2 decoder.
        var tar = OperatingSystem.IsWindows() ? Path.Combine(Environment.SystemDirectory, "tar.exe") : "tar";
        using var process = Process.Start(tar, ["-xjf", archive, "-C", into]);
        await process.WaitForExitAsync();
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"tar failed on {archive}");
        File.Delete(archive);
    }

    private static short[] Read(string path)
    {
        var (rate, channels, format, data) = Chunks(File.ReadAllBytes(path));
        var mono = format == 3
            ? PcmConvert.Float32ToMonoPcm16(data, channels)
            : PcmConvert.DownmixToMono(PcmConvert.BytesToShorts(data), channels);
        if (rate == Rate)
            return mono;
        var resampler = new LinearResampler(rate, Rate);
        var buffer = new short[resampler.GetMaxOutputCount(mono.Length)];
        return buffer[..resampler.Resample(mono, buffer)];
    }

    // FLEURS stores IEEE float WAV (format 3), which WavFile.Read refuses.
    private static (int Rate, int Channels, int Format, byte[] Data) Chunks(byte[] wav)
    {
        int rate = 0, channels = 0, format = 0;
        for (var at = 12; at + 8 <= wav.Length;)
        {
            var id = Encoding.ASCII.GetString(wav, at, 4);
            var size = BitConverter.ToInt32(wav, at + 4);
            if (id == "fmt ")
            {
                format = BitConverter.ToInt16(wav, at + 8);
                channels = BitConverter.ToInt16(wav, at + 10);
                rate = BitConverter.ToInt32(wav, at + 12);
            }
            else if (id == "data")
            {
                return (rate, channels, format, wav[(at + 8)..Math.Min(wav.Length, at + 8 + size)]);
            }

            at += 8 + size + size % 2;
        }

        throw new InvalidDataException($"No data chunk in WAV.");
    }
}
