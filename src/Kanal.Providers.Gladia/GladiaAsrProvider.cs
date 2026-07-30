using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Kanal.Core.Providers;

namespace Kanal.Providers.Gladia;

/// <summary>
/// Gladia live v2: one WebSocket session delivers streaming transcripts with
/// language detection and translation, so Caps.Translation = true and the
/// orchestrator never invokes a separate MT provider.
/// NOTE: the wire format is isolated in <see cref="GladiaWire"/> and must be
/// verified against the live API during D0-B — adjust there, not in callers.
/// </summary>
public sealed class GladiaAsrProvider : IAsrProvider, IDisposable
{
    private readonly GladiaOptions _options;
    private readonly HttpClient _http;
    private readonly bool _ownsHttp;

    public GladiaAsrProvider(GladiaOptions options, HttpClient? http = null)
    {
        _options = options;
        _ownsHttp = http is null;
        _http = http ?? new HttpClient();
    }

    public string Id => "gladia";

    public AsrCapabilities Caps { get; } = new(
        Streaming: true,
        Diarization: true,
        Translation: true,
        AutoLanguageDetect: true,
        Languages: new HashSet<string> { "zh", "de", "pl", "en", "fr", "es", "it", "pt", "ja", "ko" },
        Latency: LatencyClass.Realtime);

    public async Task<IAsrSession> StartAsync(AsrSessionOptions options, CancellationToken ct)
    {
        var body = new JsonObject
        {
            ["encoding"] = "wav/pcm",
            ["sample_rate"] = options.SampleRateHz,
            ["bit_depth"] = 16,
            ["channels"] = 1,
            ["language_config"] = new JsonObject
            {
                ["languages"] = new JsonArray(options.ExpectedLanguages.Select(l => (JsonNode)l).ToArray()),
                ["code_switching"] = true,
            },
            // without this Gladia only delivers finals — the UI needs partials for live gray text
            ["messages_config"] = new JsonObject
            {
                ["receive_partial_transcripts"] = true,
                ["receive_final_transcripts"] = true,
            },
            ["realtime_processing"] = new JsonObject
            {
                ["translation"] = true,
                ["translation_config"] = new JsonObject
                {
                    ["target_languages"] = new JsonArray(options.TargetLanguages.Select(l => (JsonNode)l).ToArray()),
                },
            },
        };
        if (_options.Model is not null)
            body["model"] = _options.Model;
        if (_options.ExtraConfig is not null)
        {
            foreach (var (key, value) in _options.ExtraConfig)
                body[key] = value?.DeepClone();
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, $"{_options.BaseUrl}/v2/live")
        {
            Content = JsonContent.Create(body),
        };
        request.Headers.Add("x-gladia-key", _options.ApiKey);

        using var response = await _http.SendAsync(request, ct);
        var payload = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException(
                $"Gladia session init failed ({(int)response.StatusCode}): {payload}");

        using var doc = JsonDocument.Parse(payload);
        var url = doc.RootElement.TryGetProperty("url", out var urlProp)
            ? urlProp.GetString()
            : null;
        if (string.IsNullOrEmpty(url))
            throw new InvalidOperationException($"Gladia session init returned no websocket url: {payload}");

        var session = new GladiaAsrSession(new Uri(url), _options.MaxReconnectAttempts);
        await session.ConnectAsync(ct);
        return session;
    }

    public void Dispose()
    {
        if (_ownsHttp)
            _http.Dispose();
    }
}
