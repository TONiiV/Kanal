using System.Text;
using System.Text.Json;

namespace Kanal.Core.Relay;

/// <summary>
/// Publishes room messages over Supabase Realtime broadcast via its stateless REST
/// endpoint — the host only ever sends, so no websocket or SDK is needed. Clients
/// subscribe to the channel (topic = room id) with supabase-js.
/// Late join / reconnect is served by the host republishing room.snapshot periodically.
/// </summary>
public sealed class SupabaseRelayPublisher : IRelayPublisher
{
    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private readonly Uri _endpoint;
    private readonly string _apiKey;
    private readonly string _topic;

    public SupabaseRelayPublisher(string supabaseUrl, string apiKey, string topic, HttpClient? http = null)
    {
        _endpoint = new Uri(supabaseUrl.TrimEnd('/') + "/realtime/v1/api/broadcast");
        _apiKey = apiKey;
        _topic = topic;
        _ownsHttp = http is null;
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
    }

    public async Task PublishAsync(RelayMessage message, CancellationToken ct = default)
    {
        var body = JsonSerializer.Serialize(new
        {
            messages = new[]
            {
                new
                {
                    topic = _topic,
                    @event = "kanal",
                    payload = JsonSerializer.SerializeToElement(message, RelayJson.Options),
                },
            },
        });

        using var request = new HttpRequestMessage(HttpMethod.Post, _endpoint)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        request.Headers.Add("apikey", _apiKey);

        using var response = await _http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
        {
            var detail = await response.Content.ReadAsStringAsync(ct);
            throw new InvalidOperationException(
                $"Relay broadcast failed ({(int)response.StatusCode}): {detail}");
        }
    }

    public ValueTask DisposeAsync()
    {
        if (_ownsHttp)
            _http.Dispose();
        return ValueTask.CompletedTask;
    }
}
