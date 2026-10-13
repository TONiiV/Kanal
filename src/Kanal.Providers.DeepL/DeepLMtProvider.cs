using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using Kanal.Core.Models;
using Kanal.Core.Providers;

namespace Kanal.Providers.DeepL;

public sealed class DeepLMtProvider : IMtProvider
{
    // DeepL answers a target outside this list with 400; skipping it keeps one odd room language from failing the others.
    private static readonly HashSet<string> Targets = new(StringComparer.Ordinal)
    {
        "AR", "BG", "CS", "DA", "DE", "EL", "EN-GB", "EN-US", "ES", "ES-419", "ET", "FI", "FR", "HE", "HU", "ID",
        "IT", "JA", "KO", "LT", "LV", "NB", "NL", "PL", "PT-BR", "PT-PT", "RO", "RU", "SK", "SL", "SV", "TH", "TR",
        "UK", "VI", "ZH-HANS", "ZH-HANT",
    };

    private static readonly Dictionary<string, string> TargetOverrides = new(StringComparer.OrdinalIgnoreCase)
    {
        ["zh"] = "ZH-HANS",
        ["en"] = "EN-GB",
        ["pt"] = "PT-PT",
    };

    private readonly string _key;
    private readonly Uri _endpoint;
    private readonly HttpClient _http;

    public DeepLMtProvider(string apiKey, HttpClient? http = null)
    {
        _key = apiKey.Trim();
        _endpoint = new Uri(_key.EndsWith(":fx", StringComparison.Ordinal)
            ? "https://api-free.deepl.com/v2/translate"
            : "https://api.deepl.com/v2/translate");
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
    }

    public string Id => "deepl";

    public async Task<IReadOnlyDictionary<string, string>> TranslateAsync(
        string text,
        string from,
        IReadOnlyList<string> to,
        IReadOnlyList<Utterance> context,
        CancellationToken ct)
    {
        var source = from.Split('-')[0].ToUpperInvariant();
        var recent = context.Count == 0 ? null : string.Join('\n', context.Select(u => u.SrcText));

        var requests = to
            .Where(room => !string.Equals(room, from, StringComparison.OrdinalIgnoreCase))
            .Select(room => (Room: room, Target: TargetCode(room)))
            .Where(t => Targets.Contains(t.Target))
            .Select(async t => (t.Room, Text: await TranslateOneAsync(text, source, t.Target, recent, ct)));

        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (room, translated) in await Task.WhenAll(requests))
        {
            if (translated.Length > 0)
                result[room] = translated;
        }

        return result;
    }

    private static string TargetCode(string room) =>
        TargetOverrides.TryGetValue(room, out var mapped) ? mapped : room.ToUpperInvariant();

    private async Task<string> TranslateOneAsync(
        string text, string source, string target, string? context, CancellationToken ct)
    {
        var body = new JsonObject
        {
            ["text"] = new JsonArray(text),
            ["source_lang"] = source,
            ["target_lang"] = target,
        };
        if (context is not null)
            body["context"] = context;

        using var request = new HttpRequestMessage(HttpMethod.Post, _endpoint)
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("DeepL-Auth-Key", _key);

        using var response = await _http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
            throw Failure(response.StatusCode);

        var json = JsonNode.Parse(await response.Content.ReadAsStringAsync(ct));
        return json?["translations"]?[0]?["text"]?.GetValue<string>() ?? "";
    }

    private static MtProviderException Failure(HttpStatusCode status) => status switch
    {
        HttpStatusCode.Forbidden => new("DeepL rejected the API key (403).", MtProviderException.InvalidKey),
        (HttpStatusCode)456 => new("The DeepL quota is used up (456).", MtProviderException.QuotaExceeded),
        _ => new($"DeepL translation failed ({(int)status})."),
    };
}
