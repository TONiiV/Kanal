using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Kanal.Core.Meetings;

namespace Kanal.DiarizationBenchmark;

[ExcludeFromCodeCoverage]
internal sealed class ForbiddenException(string message) : Exception(message);

[ExcludeFromCodeCoverage]
internal static class Cloud
{
    public const string Gladia = "gladia";
    public const string Scribe = "elevenlabs-scribe-v2";

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(20) };

    public static List<string> Deletions { get; } = [];

    public static string? Key(string name) =>
        Environment.GetEnvironmentVariable(name)
        ?? (OperatingSystem.IsWindows() ? Environment.GetEnvironmentVariable(name, EnvironmentVariableTarget.User) : null);

    // Raw responses hold transcript text, so they stay in the data folder and double as a cache.
    public static async Task<IReadOnlyList<SpeakerTurn>> Diarize(string service, string data, string id, string wav, int? hint, string[] languages)
    {
        var stem = Path.Combine(data, "cloud", $"{service}-{id}-{(hint is null ? "none" : $"hint{hint}")}");
        if (File.Exists(stem + ".rttm"))
            return RttmTurns(await File.ReadAllLinesAsync(stem + ".rttm"));
        var raw = stem + ".json";
        if (!File.Exists(raw))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(raw)!);
            var body = service switch
            {
                Gladia => await GladiaRun(wav, hint, languages, id),
                Scribe => await ScribeRun(wav, hint, id),
                _ => throw new FileNotFoundException($"No RTTM file for local system {service}.", stem + ".rttm"),
            };
            await File.WriteAllTextAsync(raw, body);
        }

        var json = JsonNode.Parse(await File.ReadAllTextAsync(raw))!;
        return service == Gladia ? GladiaTurns(json) : ScribeTurns(json);
    }

    private static async Task<string> GladiaRun(string wav, int? hint, string[] languages, string id)
    {
        var key = Key("GLADIA_API_KEY") ?? throw new InvalidOperationException("GLADIA_API_KEY is not set.");
        using var form = new MultipartFormDataContent();
        var audio = new StreamContent(File.OpenRead(wav));
        audio.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
        form.Add(audio, "audio", Path.GetFileName(wav));
        var upload = await Send(HttpMethod.Post, "https://api.gladia.io/v2/upload", form, "x-gladia-key", key);
        var start = new JsonObject
        {
            ["audio_url"] = upload["audio_url"]!.GetValue<string>(),
            ["diarization"] = true,
            ["language_config"] = new JsonObject
            {
                ["languages"] = new JsonArray([.. languages.Select(l => (JsonNode)l)]),
                ["code_switching"] = languages.Length > 1,
            },
        };
        if (hint is not null)
            start["diarization_config"] = new JsonObject { ["number_of_speakers"] = hint };
        var job = await Send(HttpMethod.Post, "https://api.gladia.io/v2/pre-recorded",
            new StringContent(start.ToJsonString(), Encoding.UTF8, "application/json"), "x-gladia-key", key);
        var url = $"https://api.gladia.io/v2/pre-recorded/{job["id"]!.GetValue<string>()}";
        try
        {
            while (true)
            {
                await Task.Delay(TimeSpan.FromSeconds(5));
                using var poll = new HttpRequestMessage(HttpMethod.Get, url);
                poll.Headers.Add("x-gladia-key", key);
                using var response = await Http.SendAsync(poll);
                var text = await response.Content.ReadAsStringAsync();
                if (!response.IsSuccessStatusCode)
                    throw new HttpRequestException($"Gladia poll answered {(int)response.StatusCode}");
                var status = JsonNode.Parse(text)!["status"]?.GetValue<string>();
                if (status == "done")
                    return text;
                if (status == "error")
                    throw new HttpRequestException($"Gladia job failed: {JsonNode.Parse(text)!["error_code"]}");
            }
        }
        finally
        {
            using var delete = new HttpRequestMessage(HttpMethod.Delete, url);
            delete.Headers.Add("x-gladia-key", key);
            using var response = await Http.SendAsync(delete);
            lock (Deletions)
                Deletions.Add($"Gladia {id} hint {hint?.ToString() ?? "none"}: DELETE job answered {(int)response.StatusCode}");
        }
    }

    private static async Task<string> ScribeRun(string wav, int? hint, string id)
    {
        var key = Key("ELEVENLABS_API_KEY") ?? throw new InvalidOperationException("ELEVENLABS_API_KEY is not set.");
        using var form = new MultipartFormDataContent
        {
            { new StringContent("scribe_v2"), "model_id" },
            { new StringContent("true"), "diarize" },
            { new StringContent("word"), "timestamps_granularity" },
            { new StringContent("false"), "tag_audio_events" },
        };
        if (hint is not null)
            form.Add(new StringContent(hint.Value.ToString()), "num_speakers");
        var audio = new StreamContent(File.OpenRead(wav));
        audio.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
        form.Add(audio, "file", Path.GetFileName(wav));
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.elevenlabs.io/v1/speech-to-text") { Content = form };
        request.Headers.Add("xi-api-key", key);
        using var response = await Http.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            throw new ForbiddenException($"ElevenLabs speech-to-text answered {(int)response.StatusCode}: {Detail(text)}");
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"ElevenLabs speech-to-text answered {(int)response.StatusCode}: {Detail(text)}");
        var transcription = JsonNode.Parse(text)!["transcription_id"]?.GetValue<string>();
        string outcome;
        if (transcription is null)
            outcome = "no transcription_id in the response; nothing to delete";
        else
        {
            using var delete = new HttpRequestMessage(HttpMethod.Delete, $"https://api.elevenlabs.io/v1/speech-to-text/transcripts/{transcription}");
            delete.Headers.Add("xi-api-key", key);
            using var deleted = await Http.SendAsync(delete);
            outcome = $"DELETE transcript answered {(int)deleted.StatusCode}";
        }

        lock (Deletions)
            Deletions.Add($"ElevenLabs {id} hint {hint?.ToString() ?? "none"}: {outcome}");
        return text;
    }

    private static string Detail(string body)
    {
        try
        {
            return JsonNode.Parse(body)?["detail"]?.ToJsonString() ?? "";
        }
        catch (JsonException)
        {
            return "";
        }
    }

    private static async Task<JsonNode> Send(HttpMethod method, string url, HttpContent content, string header, string key)
    {
        using var request = new HttpRequestMessage(method, url) { Content = content };
        request.Headers.Add(header, key);
        using var response = await Http.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"{new Uri(url).Host} answered {(int)response.StatusCode}: {Detail(text)}");
        return JsonNode.Parse(text)!;
    }

    public static IReadOnlyList<SpeakerTurn> GladiaTurns(JsonNode job) =>
        [.. job["result"]!["transcription"]!["utterances"]!.AsArray().Select(u => new SpeakerTurn(
            u!["start"]!.GetValue<double>(), u["end"]!.GetValue<double>(), u["speaker"]?.GetValue<int>() ?? -1))];

    public static IReadOnlyList<SpeakerTurn> RttmTurns(IEnumerable<string> lines)
    {
        var fields = lines.Select(l => l.Split(' ', StringSplitOptions.RemoveEmptyEntries)).Where(f => f is ["SPEAKER", ..]).ToList();
        var ids = fields.Select(f => f[7]).Distinct().Select((s, i) => (s, i)).ToDictionary(x => x.s, x => x.i);
        return [.. fields.Select(f => new SpeakerTurn(double.Parse(f[3]), double.Parse(f[3]) + double.Parse(f[4]), ids[f[7]]))];
    }

    // Words of one speaker join into a turn until the speaker changes or the gap passes MaxGap.
    public const double MaxGap = 0.5;

    public static IReadOnlyList<SpeakerTurn> ScribeTurns(JsonNode result)
    {
        var words = result["words"]!.AsArray()
            .Where(w => w!["type"]?.GetValue<string>() == "word")
            .Select(w => (Start: w!["start"]!.GetValue<double>(), End: w["end"]!.GetValue<double>(), Speaker: w["speaker_id"]?.GetValue<string>() ?? "none"))
            .ToList();
        var ids = words.Select(w => w.Speaker).Distinct().Select((s, i) => (s, i)).ToDictionary(x => x.s, x => x.i);
        var turns = new List<SpeakerTurn>();
        foreach (var w in words)
        {
            var speaker = ids[w.Speaker];
            if (turns.Count > 0 && turns[^1].Speaker == speaker && w.Start - turns[^1].EndSeconds <= MaxGap)
                turns[^1] = turns[^1] with { EndSeconds = Math.Max(turns[^1].EndSeconds, w.End) };
            else
                turns.Add(new SpeakerTurn(w.Start, w.End, speaker));
        }

        return turns;
    }
}
