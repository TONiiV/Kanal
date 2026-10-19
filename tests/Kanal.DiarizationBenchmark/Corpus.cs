using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Kanal.Audio;
using SherpaOnnx;

namespace Kanal.DiarizationBenchmark;

[ExcludeFromCodeCoverage]
internal sealed record Voice(string Id, string Lang, char Gender, string Package, int Sid = 0, string? ElevenLabsId = null);

[ExcludeFromCodeCoverage]
internal sealed record Meeting(string Id, string Lang, int Speakers, bool Overlap, bool Room, float[] Samples, IReadOnlyList<Segment> Truth)
{
    public double Seconds => Samples.Length / (double)Corpus.Rate;
}

[ExcludeFromCodeCoverage]
internal static class Corpus
{
    public const int Rate = 16_000;

    public static readonly string[] Languages = ["zh", "de", "pl", "mix"];

    public static readonly int[] SpeakerCounts = [2, 3, 4, 6];

    // Each list starts with two voices of one gender, so every meeting holds a same-gender pair.
    public static IReadOnlyDictionary<string, Voice[]> Voices { get; } = new Dictionary<string, Voice[]>
    {
        ["zh"] =
        [
            new("zh-yunjian", "zh", 'm', "kokoro-multi-lang-v1_0", 49),
            new("zh-yunxi", "zh", 'm', "kokoro-multi-lang-v1_0", 50),
            new("zh-xiaobei", "zh", 'f', "kokoro-multi-lang-v1_0", 45),
            new("zh-xiaoni", "zh", 'f', "kokoro-multi-lang-v1_0", 46),
            new("zh-yunyang", "zh", 'm', "kokoro-multi-lang-v1_0", 52),
            new("zh-xiaoyi", "zh", 'f', "kokoro-multi-lang-v1_0", 48),
        ],
        ["de"] =
        [
            new("de-thorsten", "de", 'm', "vits-piper-de_DE-thorsten-medium-int8"),
            new("de-karlsson", "de", 'm', "vits-piper-de_DE-karlsson-low-int8"),
            new("de-kerstin", "de", 'f', "vits-piper-de_DE-kerstin-low-int8"),
            new("de-ramona", "de", 'f', "vits-piper-de_DE-ramona-low-int8"),
            new("de-pavoque", "de", 'm', "vits-piper-de_DE-pavoque-low-int8"),
            new("de-eva_k", "de", 'f', "vits-piper-de_DE-eva_k-x_low-int8"),
        ],
        ["pl"] =
        [
            new("pl-darkman", "pl", 'm', "vits-piper-pl_PL-darkman-medium-int8"),
            new("pl-mc_speech", "pl", 'm', "vits-piper-pl_PL-mc_speech-medium-int8"),
            new("pl-gosia", "pl", 'f', "vits-piper-pl_PL-gosia-medium-int8"),
            new("pl-zenski", "pl", 'f', "vits-piper-pl_PL-zenski_wg_glos-medium-int8"),
            new("pl-meski", "pl", 'm', "vits-piper-pl_PL-meski_wg_glos-medium-int8"),
            new("pl-justyna", "pl", 'f', "vits-piper-pl_PL-justyna_wg_glos-medium-int8"),
        ],
    };

    public static IReadOnlyList<Voice> Cast(string lang, int speakers)
    {
        if (lang != "mix")
            return Voices[lang][..speakers];
        Voice[] order = [Voices["zh"][0], Voices["de"][0], Voices["pl"][0], Voices["zh"][2], Voices["de"][2], Voices["pl"][2]];
        return order[..speakers];
    }

    private static readonly Dictionary<string, (string[] Long, string[] Short)> Lines = new()
    {
        ["zh"] = (
        [
            "我们先确认一下支架KB-4471的最新图纸版本。",
            "上一批货里有百分之三的焊缝没有通过检测。",
            "如果把壁厚从二点五毫米减到二毫米，单价可以降低多少？",
            "我们的工厂下个月产能已经排满了，最早只能六月发货。",
            "客户要求防护等级达到IP67，现在的密封圈恐怕不够。",
            "这个价格我们需要和总部再商量一下。",
            "模具的费用可以分摊到前五万件里面吗？",
            "我建议先做五十个样品，测试通过以后再批量生产。",
            "疲劳测试要做两千次循环，大概需要两周时间。",
            "材料证书和检测报告我们会随货一起发过去。",
            "运费是按到港价算，还是按到厂价算？",
            "表面处理我们可以用镀锌，也可以用粉末喷涂。",
            "粉末喷涂的盐雾测试能达到四百八十小时。",
            "交货期如果提前一周，需要加百分之五的加急费。",
            "我们上次讨论的付款方式是百分之三十预付款。",
            "包装要改成木箱，纸箱在海运时容易受潮。",
            "这个公差太紧了，零点一毫米我们很难保证。",
            "能不能把螺栓孔的位置往左移两毫米？",
            "我们需要一份完整的首件检验报告。",
            "如果质量问题是我们造成的，返工费用由我们承担。",
            "明年的预计需求量大概是多少？",
            "年用量超过十万件的话，我们可以给更好的价格。",
            "这个问题我回去和质量部门确认，明天给您答复。",
            "我们希望每个月开一次视频会议，跟进项目进度。",
            "新的焊接工艺已经在试生产线上验证过了。",
            "请把技术要求整理成一份文件发给我们。",
            "第二家供应商的报价比你们低百分之八。",
            "我们的优势是交货稳定，而且质量问题响应很快。",
            "样品的检测结果下周三之前能出来吗？",
            "那我们今天就先定下来，样品按新图纸做。",
        ],
        ["好。", "对。", "是的。", "没问题。", "好的。", "嗯，明白。", "可以。", "对对对。", "行。", "谢谢。"]),
        ["de"] = (
        [
            "Wir sollten zuerst die aktuelle Zeichnungsversion für den Halter KB-4471 bestätigen.",
            "In der letzten Lieferung haben drei Prozent der Schweißnähte die Prüfung nicht bestanden.",
            "Wie viel günstiger wird der Stückpreis, wenn die Wandstärke auf zwei Millimeter sinkt?",
            "Unsere Fertigung ist im nächsten Monat voll ausgelastet, frühestens liefern wir im Juni.",
            "Der Kunde verlangt Schutzart IP67, die jetzige Dichtung reicht dafür wahrscheinlich nicht.",
            "Über diesen Preis müssen wir noch einmal mit der Zentrale sprechen.",
            "Können die Werkzeugkosten auf die ersten fünfzigtausend Stück umgelegt werden?",
            "Ich schlage vor, zuerst fünfzig Muster zu bauen und erst nach dem Test in Serie zu gehen.",
            "Der Dauerlauftest umfasst zweitausend Zyklen und dauert etwa zwei Wochen.",
            "Die Materialzeugnisse und Prüfberichte schicken wir mit der Ware mit.",
            "Rechnen wir die Fracht frei Hafen oder frei Werk?",
            "Bei der Oberfläche können wir verzinken oder pulverbeschichten.",
            "Die Pulverbeschichtung erreicht im Salzsprühtest vierhundertachtzig Stunden.",
            "Wenn die Lieferung eine Woche früher kommen soll, berechnen wir fünf Prozent Eilzuschlag.",
            "Beim letzten Mal hatten wir dreißig Prozent Anzahlung vereinbart.",
            "Die Verpackung muss auf Holzkisten umgestellt werden, Kartons werden auf See feucht.",
            "Diese Toleranz ist zu eng, ein Zehntel Millimeter können wir kaum garantieren.",
            "Könnten Sie die Bohrung für die Schraube um zwei Millimeter nach links verschieben?",
            "Wir brauchen einen vollständigen Erstmusterprüfbericht.",
            "Wenn der Qualitätsfehler bei uns liegt, übernehmen wir die Nacharbeit.",
            "Mit welchem Bedarf rechnen Sie für das nächste Jahr?",
            "Ab hunderttausend Stück im Jahr können wir Ihnen einen besseren Preis anbieten.",
            "Das kläre ich mit unserer Qualitätsabteilung und melde mich morgen bei Ihnen.",
            "Wir möchten einmal im Monat eine Videokonferenz zum Projektstand machen.",
            "Das neue Schweißverfahren wurde auf der Pilotlinie bereits freigegeben.",
            "Bitte fassen Sie die technischen Anforderungen in einem Dokument zusammen.",
            "Der zweite Lieferant liegt mit seinem Angebot acht Prozent unter Ihnen.",
            "Unser Vorteil ist die stabile Lieferung und eine schnelle Reaktion bei Reklamationen.",
            "Liegen die Prüfergebnisse der Muster bis nächsten Mittwoch vor?",
            "Dann halten wir heute fest, dass die Muster nach der neuen Zeichnung gebaut werden.",
        ],
        ["Ja.", "Genau.", "Okay.", "Stimmt.", "Ja, gut.", "Alles klar.", "Richtig.", "Einverstanden.", "Moment.", "Danke."]),
        ["pl"] = (
        [
            "Najpierw potwierdźmy aktualną wersję rysunku wspornika KB-4471.",
            "W ostatniej dostawie trzy procent spoin nie przeszło kontroli.",
            "O ile spadnie cena jednostkowa, jeśli grubość ścianki zmniejszymy do dwóch milimetrów?",
            "Nasza produkcja jest w przyszłym miesiącu w pełni obłożona, najwcześniej wyślemy w czerwcu.",
            "Klient wymaga stopnia ochrony IP67, obecna uszczelka raczej nie wystarczy.",
            "Tę cenę musimy jeszcze raz omówić z centralą.",
            "Czy koszt narzędzia można rozłożyć na pierwsze pięćdziesiąt tysięcy sztuk?",
            "Proponuję najpierw zrobić pięćdziesiąt próbek, a produkcję seryjną dopiero po testach.",
            "Test zmęczeniowy obejmuje dwa tysiące cykli i trwa około dwóch tygodni.",
            "Atesty materiałowe i raporty z badań wyślemy razem z towarem.",
            "Liczymy transport do portu czy do zakładu?",
            "Powierzchnię możemy ocynkować albo pomalować proszkowo.",
            "Powłoka proszkowa wytrzymuje czterysta osiemdziesiąt godzin w teście mgły solnej.",
            "Jeśli dostawa ma być tydzień wcześniej, doliczymy pięć procent za ekspres.",
            "Ostatnio uzgodniliśmy trzydzieści procent przedpłaty.",
            "Opakowanie trzeba zmienić na skrzynie drewniane, kartony na morzu wilgotnieją.",
            "Ta tolerancja jest za wąska, jednej dziesiątej milimetra nie zagwarantujemy.",
            "Czy moglibyście przesunąć otwór na śrubę o dwa milimetry w lewo?",
            "Potrzebujemy pełnego raportu z badania pierwszej sztuki.",
            "Jeśli wada jakościowa jest po naszej stronie, pokryjemy koszty poprawek.",
            "Jakiego zapotrzebowania spodziewacie się w przyszłym roku?",
            "Powyżej stu tysięcy sztuk rocznie możemy zaproponować lepszą cenę.",
            "Wyjaśnię to z naszym działem jakości i odezwę się jutro.",
            "Chcielibyśmy raz w miesiącu robić wideokonferencję o stanie projektu.",
            "Nowy proces spawania został już zatwierdzony na linii pilotażowej.",
            "Proszę zebrać wymagania techniczne w jednym dokumencie.",
            "Drugi dostawca jest w swojej ofercie osiem procent tańszy od was.",
            "Naszą przewagą są stabilne dostawy i szybka reakcja na reklamacje.",
            "Czy wyniki badań próbek będą gotowe do przyszłej środy?",
            "W takim razie ustalamy dziś, że próbki powstaną według nowego rysunku.",
        ],
        ["Tak.", "Dobrze.", "Okej.", "Jasne.", "Zgadza się.", "Rozumiem.", "Dokładnie.", "Tak, tak.", "Chwileczkę.", "Dziękuję."]),
    };

    private const double SpeechTarget = 140;

    private sealed record Turn(Voice Voice, string Text, double Pause, double Overlap);

    public static IEnumerable<Meeting> Meetings(string data, IReadOnlyCollection<string> languages, IReadOnlyCollection<int> counts,
        IReadOnlyCollection<string> variants)
    {
        foreach (var lang in Languages.Where(languages.Contains))
        foreach (var n in counts.Where(SpeakerCounts.Contains))
        {
            var turns = Script(lang, n);
            var clips = turns.Select(t => Synthesised(data, t.Voice, t.Text)).ToList();
            foreach (var variant in variants)
                yield return Build($"{lang}-{n}-{variant}", lang, n, variant.Contains("overlap"), variant.Contains("room"), turns, clips);
        }
    }

    private static List<Turn> Script(string lang, int n)
    {
        var rng = new Random(lang.Sum(c => c) * 31 + n);
        var cast = Cast(lang, n);
        var next = cast.Select(v => v.Lang).Distinct().ToDictionary(l => l, _ => rng.Next(30));
        var turns = new List<Turn>();
        Voice? previous = null;
        var wasShort = true;
        var speech = 0.0;
        while (speech < SpeechTarget)
        {
            var choices = cast.Where(v => v != previous).ToList();
            var voice = choices[rng.Next(choices.Count)];
            var isShort = !wasShort && rng.NextDouble() < 0.2;
            var (longLines, shortLines) = Lines[voice.Lang];
            var text = isShort ? shortLines[rng.Next(shortLines.Length)] : longLines[next[voice.Lang]++ % longLines.Length];
            var pause = 0.3 + rng.NextDouble() * 0.9;
            var overlap = rng.NextDouble() < 0.35 ? 0.5 + rng.NextDouble() * 1.5 : 0;
            turns.Add(new Turn(voice, text, pause, overlap));
            speech += isShort ? 0.6 : 4.5;
            (previous, wasShort) = (voice, isShort);
        }

        return turns;
    }

    private static Meeting Build(string id, string lang, int n, bool overlap, bool room, List<Turn> turns, List<float[]> clips, List<string>? castIds = null)
    {
        var truth = new List<Segment>();
        var lastEnd = new Dictionary<string, double>();
        double cursor = 0.5, previousStart = 0;
        for (var i = 0; i < turns.Count; i++)
        {
            var turn = turns[i];
            var seconds = clips[i].Length / (double)Rate;
            var start = i == 0 ? cursor : cursor + turn.Pause;
            if (overlap && i > 0 && turn.Overlap > 0)
                start = Math.Max(cursor - turn.Overlap, previousStart + 0.3);
            start = Math.Max(start, lastEnd.GetValueOrDefault(turn.Voice.Id) + 0.2);
            start = Math.Round(start * Rate) / Rate;
            truth.Add(new Segment(start, start + seconds, turn.Voice.Id));
            lastEnd[turn.Voice.Id] = start + seconds;
            cursor = Math.Max(cursor, start + seconds);
            previousStart = start;
        }

        var cast = castIds ?? Cast(lang, n).Select(v => v.Id).ToList();
        var samples = new float[(int)((cursor + 1.0) * Rate)];
        for (var i = 0; i < turns.Count; i++)
        {
            var gain = room ? (float)Math.Pow(10, -RoomGainDb[cast.IndexOf(turns[i].Voice.Id)] / 20) : 1f;
            var at = (int)Math.Round(truth[i].Start * Rate);
            for (var k = 0; k < clips[i].Length; k++)
                samples[at + k] += clips[i][k] * gain;
        }

        if (room)
            samples = Room(samples, new Random(id.Sum(c => c)));
        var pcm16 = samples.Select(s => (short)Math.Clamp(s * 32767f, short.MinValue, short.MaxValue) / 32768f).ToArray();
        return new Meeting(id, lang, n, overlap, room, pcm16, truth);
    }

    // ElevenLabs premade voices; the ids are public and stable across accounts.
    private static readonly Dictionary<string, string> ElevenLabsVoices = new()
    {
        ["zh-wang"] = "nPczCjzI2devNBz1zQrb",
        ["zh-li"] = "pFZP5JQG7iQjIQuC4Bku",
        ["de-becker"] = "onwK4e9ZLuTAKqWW03F9",
        ["de-schulz"] = "JBFqnCBsd6RMkjVDRZzb",
        ["pl-nowak"] = "EXAVITQu4vr4xnSDxMaL",
        ["pl-kowalska"] = "XrExE9yKIg1WjnnlVkGX",
    };

    public static int ElevenLabsCharacters { get; private set; }

    public static IEnumerable<Meeting> Scripted(string data, string path)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var root = doc.RootElement;
        var id = root.GetProperty("id").GetString()!;
        var voices = root.GetProperty("speakers").EnumerateArray().Select(sp =>
        {
            var speaker = sp.GetProperty("id").GetString()!;
            return new Voice(speaker, sp.GetProperty("lang").GetString()!, sp.GetProperty("gender").GetString()![0], "elevenlabs",
                ElevenLabsId: ElevenLabsVoices[speaker]);
        }).ToList();
        var rng = new Random(7);
        var turns = root.GetProperty("turns").EnumerateArray().Select(t => new Turn(
            voices.Single(v => v.Id == t.GetProperty("speaker").GetString()),
            t.GetProperty("text").GetString()!,
            0.2 + rng.NextDouble() * 0.6,
            t.TryGetProperty("overlapSeconds", out var o) ? o.GetDouble() : 0)).ToList();
        var clips = turns.Select(t => Synthesised(data, t.Voice, t.Text)).ToList();
        foreach (var room in new[] { false, true })
            yield return Build($"{id}-{(room ? "room" : "clean")}", "mix", voices.Count, true, room, turns, clips, voices.Select(v => v.Id).ToList());
    }

    private static readonly double[] RoomGainDb = [0, 4, 2, 6, 3, 5];

    public const string RoomDescription =
        "per-speaker level 0/−4/−2/−6/−3/−5 dB (distance to one table microphone); Schroeder reverb (4 combs, 2 all-passes, RT60 0.5 s, 30 % wet); one-pole low-pass at 3.5 kHz; low-passed white noise (1 kHz corner) at 15 dB SNR against the mean speech level";

    private static float[] Room(float[] dry, Random rng)
    {
        int[] combDelays = [565, 587, 541, 516];
        int[] allpassDelays = [202, 82];
        var wet = new float[dry.Length];
        foreach (var d in combDelays)
        {
            var g = (float)Math.Pow(10, -3.0 * d / (0.5 * Rate));
            var buffer = new float[dry.Length];
            for (var i = 0; i < dry.Length; i++)
            {
                buffer[i] = dry[i] + (i >= d ? g * buffer[i - d] : 0);
                wet[i] += buffer[i] / combDelays.Length;
            }
        }

        foreach (var d in allpassDelays)
        {
            var output = new float[wet.Length];
            for (var i = 0; i < wet.Length; i++)
                output[i] = -0.5f * wet[i] + (i >= d ? wet[i - d] + 0.5f * output[i - d] : 0);
            wet = output;
        }

        var mixed = new float[dry.Length];
        var a = (float)Math.Exp(-2 * Math.PI * 3500 / Rate);
        float y = 0;
        for (var i = 0; i < dry.Length; i++)
        {
            y = (1 - a) * (0.7f * dry[i] + 0.3f * wet[i]) + a * y;
            mixed[i] = y;
        }

        var speechRms = Math.Sqrt(mixed.Where(s => Math.Abs(s) > 1e-3).Select(s => (double)s * s).DefaultIfEmpty(0).Average());
        var noise = new float[dry.Length];
        var b = (float)Math.Exp(-2 * Math.PI * 1000 / Rate);
        float n = 0;
        for (var i = 0; i < noise.Length; i++)
        {
            n = (1 - b) * (float)(rng.NextDouble() * 2 - 1) + b * n;
            noise[i] = n;
        }

        var noiseRms = Math.Sqrt(noise.Select(s => (double)s * s).Average());
        var scale = (float)(speechRms / Math.Pow(10, 15 / 20.0) / noiseRms);
        for (var i = 0; i < mixed.Length; i++)
            mixed[i] = Math.Clamp(mixed[i] + scale * noise[i], -1f, 1f);
        return mixed;
    }

    private static readonly Dictionary<string, OfflineTts> Engines = [];

    public static float[] Synthesised(string data, Voice voice, string text)
    {
        var hash = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(text)))[..12];
        var path = Path.Combine(data, "clips", voice.Id, hash + ".wav");
        if (!File.Exists(path))
        {
            short[] pcm;
            if (voice.ElevenLabsId is { } elevenLabsId)
            {
                pcm = ElevenLabs(elevenLabsId, text);
            }
            else
            {
                var audio = Engine(data, voice.Package).Generate(text, 1.0f, voice.Sid);
                pcm = audio.Samples.Select(s => (short)Math.Clamp(s * 32767f, short.MinValue, short.MaxValue)).ToArray();
                if (audio.SampleRate != Rate)
                {
                    var resampler = new LinearResampler(audio.SampleRate, Rate);
                    var buffer = new short[resampler.GetMaxOutputCount(pcm.Length)];
                    pcm = buffer[..resampler.Resample(pcm, buffer)];
                }
            }

            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using var file = File.Create(path);
            WavFile.Write(file, PcmConvert.ShortsToBytes(Level(Trim(pcm))), Rate, 1);
        }

        using var stream = File.OpenRead(path);
        return [.. PcmConvert.BytesToShorts(WavFile.Read(stream).Pcm16).Select(s => s / 32768f)];
    }

    private static short[] ElevenLabs(string voiceId, string text)
    {
        var key = Cloud.Key("ELEVENLABS_API_KEY") ?? throw new InvalidOperationException("ELEVENLABS_API_KEY is not set.");
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
        using var request = new HttpRequestMessage(HttpMethod.Post,
            $"https://api.elevenlabs.io/v1/text-to-speech/{voiceId}?output_format=pcm_16000")
        {
            Content = new StringContent(JsonSerializer.Serialize(new { text, model_id = "eleven_multilingual_v2" }), Encoding.UTF8, "application/json"),
        };
        request.Headers.Add("xi-api-key", key);
        using var response = http.Send(request);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"ElevenLabs answered {(int)response.StatusCode}: {response.Content.ReadAsStringAsync().GetAwaiter().GetResult()}");
        if (response.Headers.TryGetValues("character-cost", out var cost))
            ElevenLabsCharacters += int.Parse(cost.First());
        return PcmConvert.BytesToShorts(response.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult());
    }

    public static int NativeRate(string data, Voice voice) => Engine(data, voice.Package).SampleRate;

    private static OfflineTts Engine(string data, string package)
    {
        if (!Engines.TryGetValue(package, out var tts))
            Engines[package] = tts = Load(data, package);
        return tts;
    }

    // Speech boundaries are the ground truth, so the synthesiser's silence padding goes.
    private static short[] Trim(short[] pcm)
    {
        const int frame = Rate / 100;
        var rms = new List<double>();
        for (var i = 0; i + frame <= pcm.Length; i += frame)
        {
            double sum = 0;
            for (var j = i; j < i + frame; j++)
                sum += (double)pcm[j] * pcm[j];
            rms.Add(Math.Sqrt(sum / frame));
        }

        var threshold = rms.DefaultIfEmpty().Max() / 100;
        var first = rms.FindIndex(r => r >= threshold);
        var last = rms.FindLastIndex(r => r >= threshold);
        return first < 0 ? pcm : pcm[Math.Max(0, (first - 5) * frame)..Math.Min(pcm.Length, (last + 6) * frame)];
    }

    // One RMS for every clip: a level step between voices is a cue a real room does not give.
    private static short[] Level(short[] pcm)
    {
        var rms = Math.Sqrt(pcm.Select(s => (double)s * s).Average());
        var gain = rms == 0 ? 1 : 32768 * Math.Pow(10, -23 / 20.0) / rms;
        return [.. pcm.Select(s => (short)Math.Clamp(s * gain, short.MinValue, short.MaxValue))];
    }

    public static double MedianF0(float[] samples)
    {
        const int frame = 640;
        var f0 = new List<double>();
        var peak = samples.Select(Math.Abs).DefaultIfEmpty().Max();
        for (var i = 0; i + frame <= samples.Length; i += frame / 2)
        {
            var span = samples.AsSpan(i, frame);
            double energy = 0;
            foreach (var s in span)
                energy += s * s;
            if (Math.Sqrt(energy / frame) < peak * 0.1)
                continue;
            double best = 0;
            var bestLag = 0;
            for (var lag = Rate / 400; lag <= Rate / 60; lag++)
            {
                double dot = 0;
                for (var k = 0; k + lag < frame; k++)
                    dot += span[k] * span[k + lag];
                if (dot / energy > best)
                    (best, bestLag) = (dot / energy, lag);
            }

            if (best > 0.5)
                f0.Add(Rate / (double)bestLag);
        }

        return f0.Count == 0 ? 0 : f0.Order().ElementAt(f0.Count / 2);
    }

    private static OfflineTts Load(string data, string package)
    {
        var dir = Path.Combine(data, "tts", package);
        if (!Directory.Exists(dir))
            Extract($"https://github.com/k2-fsa/sherpa-onnx/releases/download/tts-models/{package}.tar.bz2", Path.Combine(data, "tts"));
        var config = new OfflineTtsConfig();
        config.Model.NumThreads = 4;
        if (package.StartsWith("kokoro", StringComparison.Ordinal))
        {
            config.Model.Kokoro.Model = Path.Combine(dir, "model.onnx");
            config.Model.Kokoro.Voices = Path.Combine(dir, "voices.bin");
            config.Model.Kokoro.Tokens = Path.Combine(dir, "tokens.txt");
            config.Model.Kokoro.DataDir = Path.Combine(dir, "espeak-ng-data");
            config.Model.Kokoro.DictDir = Path.Combine(dir, "dict");
            config.Model.Kokoro.Lexicon = string.Join(',', Path.Combine(dir, "lexicon-us-en.txt"), Path.Combine(dir, "lexicon-zh.txt"));
        }
        else
        {
            config.Model.Vits.Model = Directory.GetFiles(dir, "*.onnx").Single();
            config.Model.Vits.Tokens = Path.Combine(dir, "tokens.txt");
            config.Model.Vits.DataDir = Path.Combine(dir, "espeak-ng-data");
        }

        return new OfflineTts(config);
    }

    private static void Extract(string url, string into)
    {
        Directory.CreateDirectory(into);
        var archive = Path.Combine(into, Path.GetFileName(url));
        Console.WriteLine($"Downloading {url}");
        using (var http = new HttpClient { Timeout = TimeSpan.FromMinutes(30) })
        using (var file = File.Create(archive))
            http.GetStreamAsync(url).GetAwaiter().GetResult().CopyTo(file);
        // bsdtar ships with Windows 10+ and macOS and reads bzip2; .NET has no bzip2 decoder.
        var tar = OperatingSystem.IsWindows() ? Path.Combine(Environment.SystemDirectory, "tar.exe") : "tar";
        using var process = Process.Start(tar, ["-xjf", archive, "-C", into]);
        process.WaitForExit();
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"tar failed on {archive}");
        File.Delete(archive);
    }
}
