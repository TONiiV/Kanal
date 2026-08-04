using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace Kanal.Core.Relay;

/// <summary>
/// Publishes through the authenticated Kanal gateway. Supabase credentials exist only inside
/// that server-side function; the desktop receives a short-lived, room-scoped host ticket.
/// </summary>
public sealed class GatewayRelayPublisher : IRelayPublisher
{
    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private readonly Uri _publishEndpoint;
    private readonly string _hostTicket;

    private GatewayRelayPublisher(
        string gatewayUrl,
        string hostTicket,
        HttpClient http,
        bool ownsHttp)
    {
        _publishEndpoint = ActionEndpoint(gatewayUrl, "publish");
        _hostTicket = hostTicket;
        _http = http;
        _ownsHttp = ownsHttp;
    }

    public static async Task<GatewayRoom> CreateRoomAsync(
        string gatewayUrl,
        string bootstrapToken,
        string roomId,
        string verificationKey,
        HttpClient? http = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gatewayUrl);
        ArgumentException.ThrowIfNullOrWhiteSpace(bootstrapToken);
        ArgumentException.ThrowIfNullOrWhiteSpace(roomId);
        ArgumentException.ThrowIfNullOrWhiteSpace(verificationKey);
        if (Encoding.UTF8.GetByteCount(bootstrapToken) < 32)
            throw new ArgumentException(
                "Relay host token must contain at least 32 bytes.",
                nameof(bootstrapToken));

        var ownsHttp = http is null;
        http ??= new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        try
        {
            using var request = new HttpRequestMessage(
                HttpMethod.Post,
                ActionEndpoint(gatewayUrl, "create"))
            {
                Content = JsonContent.Create(new { roomId, verificationKey }),
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bootstrapToken);

            using var response = await http.SendAsync(request, ct);
            var body = await response.Content.ReadAsStringAsync(ct);
            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException(
                    $"Relay room creation failed ({(int)response.StatusCode}): {body}");

            var created = JsonSerializer.Deserialize<CreateRoomResponse>(body, RelayJson.Options)
                          ?? throw new InvalidOperationException("Relay gateway returned an empty response.");
            if (string.IsNullOrWhiteSpace(created.HostTicket) ||
                string.IsNullOrWhiteSpace(created.InviteTicket))
                throw new InvalidOperationException("Relay gateway returned incomplete room credentials.");

            var publisher = new GatewayRelayPublisher(
                gatewayUrl, created.HostTicket, http, ownsHttp);
            return new GatewayRoom(publisher, created.InviteTicket);
        }
        catch
        {
            if (ownsHttp)
                http.Dispose();
            throw;
        }
    }

    public async Task PublishAsync(RelayMessage message, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        using var request = new HttpRequestMessage(HttpMethod.Post, _publishEndpoint)
        {
            Content = JsonContent.Create(
                new { payload = JsonSerializer.SerializeToElement(message, RelayJson.Options) }),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _hostTicket);

        using var response = await _http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
        {
            var detail = await response.Content.ReadAsStringAsync(ct);
            throw new InvalidOperationException(
                $"Relay publish failed ({(int)response.StatusCode}): {detail}");
        }
    }

    public ValueTask DisposeAsync()
    {
        if (_ownsHttp)
            _http.Dispose();
        return ValueTask.CompletedTask;
    }

    private static Uri ActionEndpoint(string gatewayUrl, string action)
    {
        if (!Uri.TryCreate(gatewayUrl, UriKind.Absolute, out var endpoint) ||
            (!string.Equals(endpoint.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) &&
             !(string.Equals(endpoint.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) &&
               endpoint.IsLoopback)) ||
            !string.IsNullOrEmpty(endpoint.UserInfo) ||
            !string.IsNullOrEmpty(endpoint.Fragment))
            throw new ArgumentException(
                "Relay gateway must be an HTTPS URL without credentials or a fragment.",
                nameof(gatewayUrl));

        var builder = new UriBuilder(endpoint);
        var existing = builder.Query.TrimStart('?');
        builder.Query = string.IsNullOrEmpty(existing)
            ? $"action={Uri.EscapeDataString(action)}"
            : $"{existing}&action={Uri.EscapeDataString(action)}";
        return builder.Uri;
    }

    private sealed record CreateRoomResponse(string HostTicket, string InviteTicket);
}

public sealed record GatewayRoom(GatewayRelayPublisher Publisher, string InviteTicket);
