using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Formats.Tar;
using System.IO.Compression;
using System.Text;
using Kanal.Audio;
using SherpaOnnx;

namespace Kanal.AsrBenchmark;

[ExcludeFromCodeCoverage]
internal sealed record Clip(string Set, string Lang, string Id, string Reference, short[] Samples)
{
    public double Seconds => Samples.Length / (double)Corpus.Rate;
}

[ExcludeFromCodeCoverage]
internal static class Corpus
{
    public const int Rate = 16_000;

    public static readonly string[] Languages = ["zh", "de", "pl", "en"];

    // FLEURS (Conneau et al. 2022), CC-BY-4.0. The revision pins the clip choice across machines.
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

    public static IReadOnlyList<Clip> Load(string data)
    {
        var manifest = Path.Combine(data, "clips.tsv");
        if (!File.Exists(manifest))
            return [];
        return File.ReadAllLines(manifest)
            .Select(line => line.Split('\t'))
            .Select(f => new Clip(f[0], f[1], f[2], f[4], Read(Path.Combine(data, f[3]))))
            .ToList();
    }

    public static readonly Dictionary<string, Func<HttpClient, string, int, Task<List<string>>>> Sets = new()
    {
        ["fleurs"] = FetchFleursAsync,
        ["meeting"] = (http, data, _) => SynthesiseMeetingAsync(http, data),
    };

    public static async Task FetchAsync(string data, int perLanguage, IReadOnlyCollection<string> sets)
    {
        Directory.CreateDirectory(data);
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
        var manifest = Path.Combine(data, "clips.tsv");
        var rows = File.Exists(manifest)
            ? File.ReadAllLines(manifest).Where(l => !sets.Contains(l.Split('	')[0])).ToList()
            : [];
        foreach (var set in sets)
            rows.AddRange(await Sets[set](http, data, perLanguage));
        await File.WriteAllLinesAsync(manifest, rows);
        Console.WriteLine($"{rows.Count} clips listed in {manifest}");
    }

    private static async Task<List<string>> FetchFleursAsync(HttpClient http, string data, int count)
    {
        var rows = new List<string>();
        foreach (var lang in Languages)
            rows.AddRange(await FetchFleursAsync(http, data, lang, count));
        return rows;
    }

    private static async Task<List<string>> FetchFleursAsync(HttpClient http, string data, string lang, int count)
    {
        var config = FleursConfig[lang];
        var root = $"https://huggingface.co/datasets/google/fleurs/resolve/{FleursRevision}/data/{config}";
        var transcripts = (await http.GetStringAsync($"{root}/test.tsv"))
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Split('\t'))
            .GroupBy(f => f[1])
            .ToDictionary(g => g.Key, g => g.First()[2]);

        var folder = Path.Combine(data, "fleurs", lang);
        Directory.CreateDirectory(folder);
        var rows = new List<string>();
        // The test archive is ~0.5 GB; reading it as a stream stops after the first clips.
        using var response = await http.GetAsync($"{root}/audio/test.tar.gz", HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();
        await using var gzip = new GZipStream(await response.Content.ReadAsStreamAsync(), CompressionMode.Decompress);
        using var tar = new TarReader(gzip);
        while (rows.Count < count && await tar.GetNextEntryAsync() is { } entry)
        {
            var name = Path.GetFileName(entry.Name);
            if (entry.DataStream is null || !name.EndsWith(".wav") || !transcripts.TryGetValue(name, out var text))
                continue;
            await using (var file = File.Create(Path.Combine(folder, name)))
                await entry.DataStream.CopyToAsync(file);
            rows.Add(string.Join('\t', "fleurs", lang, Path.GetFileNameWithoutExtension(name), $"fleurs/{lang}/{name}", text));
        }

        Console.WriteLine($"FLEURS {config}: {rows.Count} clips");
        return rows;
    }

    private static async Task<List<string>> SynthesiseMeetingAsync(HttpClient http, string data)
    {
        var rows = new List<string>();
        var folder = Path.Combine(data, "meeting");
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
                var speech = buffer[..resampler.Resample(pcm, buffer)];
                short[] padded = [.. new short[Rate * 3 / 10], .. speech, .. new short[Rate / 2]];
                var id = $"{lang}-{++index:D2}";
                await using (var file = File.Create(Path.Combine(folder, id + ".wav")))
                    WavFile.Write(file, PcmConvert.ShortsToBytes(padded), Rate, 1);
                rows.Add(string.Join('\t', "meeting", lang, id, $"meeting/{id}.wav", text));
            }

            Console.WriteLine($"Meeting {lang}: {index} clips synthesised with {voice}");
        }

        return rows;
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
