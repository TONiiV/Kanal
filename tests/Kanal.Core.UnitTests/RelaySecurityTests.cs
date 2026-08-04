using System.Net;
using System.Text.Json;
using Kanal.Core.Relay;
using Kanal.Host.Services;

namespace Kanal.Core.UnitTests;

public class RelaySecurityTests
{
    [Fact]
    public void JoinUrlCarriesOnlyGatewayCapabilityData()
    {
        var settings = new RelaySettings(
            "https://relay.example.test/kanal-relay",
            "host-token-never-put-this-in-the-url",
            "https://example.test/mobile/");

        var url = settings.BuildJoinUrl(
            "reader-ticket",
            "kanal-room-capability",
            "public-verification-key");

        Assert.Equal(
            "https://example.test/mobile/#relay=https%3A%2F%2Frelay.example.test%2Fkanal-relay" +
            "&ticket=reader-ticket&room=kanal-room-capability&vk=public-verification-key",
            url);
        Assert.DoesNotContain("supabase", url, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("host-token", url, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RepositoryShipsNoSupabaseConfiguration()
    {
        var root = FindRepositoryRoot();
        var shipped = Directory.EnumerateFiles(
                Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories)
            .Concat(Directory.EnumerateFiles(
                Path.Combine(root, "web"), "*.html", SearchOption.AllDirectories))
            .Concat([
                Path.Combine(root, "docs", "index.html"),
                Path.Combine(root, "gateway", "src", "index.ts"),
            ])
            .Select(File.ReadAllText);

        Assert.DoesNotContain(shipped, text => text.Contains("sb_publishable_"));
        Assert.DoesNotContain(shipped, text => text.Contains(".supabase.co"));
    }

    [Fact]
    public void SignedEnvelopeRoundTripsAndRejectsTampering()
    {
        using var signer = RelaySigningKey.Create();
        var envelope = signer.Sign(new RoomPausedMessage(true));

        Assert.True(RelaySigningKey.TryVerify(signer.VerificationKey, envelope, out var verified));
        Assert.True(Assert.IsType<RoomPausedMessage>(verified).Paused);

        var data = envelope.Data.ToCharArray();
        data[^1] = data[^1] == 'A' ? 'B' : 'A';
        var tampered = envelope with { Data = new string(data) };
        Assert.False(RelaySigningKey.TryVerify(signer.VerificationKey, tampered, out _));
    }

    [Fact]
    public void AKeyFromAnotherRoomCannotAuthenticateAMessage()
    {
        using var roomA = RelaySigningKey.Create();
        using var roomB = RelaySigningKey.Create();
        var envelope = roomA.Sign(new RoomClosedMessage());

        Assert.False(RelaySigningKey.TryVerify(roomB.VerificationKey, envelope, out _));
    }

    [Fact]
    public async Task GatewayCreatesARoomThenPublishesASignedEnvelope()
    {
        var handler = new CaptureHandler();
        using var http = new HttpClient(handler);
        var room = await GatewayRelayPublisher.CreateRoomAsync(
            "https://relay.example.test/kanal-relay",
            "host-bootstrap-token-with-32-bytes",
            "room-capability",
            "verification-key",
            http,
            TestContext.Current.CancellationToken);
        using var key = RelaySigningKey.Create();
        await using var signed = new SignedRelayPublisher(room.Publisher, key);

        await signed.PublishAsync(
            new RoomRecordingMessage(true),
            TestContext.Current.CancellationToken);

        Assert.Equal("reader-ticket", room.InviteTicket);
        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal("host-bootstrap-token-with-32-bytes", handler.Requests[0].Bearer);
        Assert.Equal("host-room-ticket", handler.Requests[1].Bearer);
        Assert.Equal("create", handler.Requests[0].Action);
        Assert.Equal("publish", handler.Requests[1].Action);

        using var body = JsonDocument.Parse(handler.Requests[1].Body);
        var payload = body.RootElement.GetProperty("payload");
        var envelope = Assert.IsType<SignedRelayMessage>(
            RelayJson.Deserialize(payload.GetRawText()));
        Assert.True(RelaySigningKey.TryVerify(key.VerificationKey, envelope, out var message));
        Assert.True(Assert.IsType<RoomRecordingMessage>(message).Recording);
    }

    [Fact]
    public async Task GatewayRefusesToSendTheHostTokenOverPlainHttp()
    {
        using var http = new HttpClient(new CaptureHandler());

        await Assert.ThrowsAsync<ArgumentException>(() =>
            GatewayRelayPublisher.CreateRoomAsync(
                "http://relay.example.test/kanal-relay",
                "host-bootstrap-token-with-32-bytes",
                "room-capability",
                "verification-key",
                http,
                TestContext.Current.CancellationToken));
    }

    [Fact]
    public void MobilePageUsesOnlyTheAuthenticatedGateway()
    {
        var root = FindRepositoryRoot();
        var web = File.ReadAllText(Path.Combine(root, "web", "index.html"));
        var docs = File.ReadAllText(Path.Combine(root, "docs", "index.html"));

        Assert.Equal(web, docs);
        Assert.Contains("crypto.subtle.verify", web);
        Assert.Contains("await verifyEnvelope", web);
        Assert.Contains("new WebSocket", web);
        Assert.Contains("invite.get(\"ticket\")", web);
        Assert.DoesNotContain("supabase", web, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("apikey", web, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The gateway Worker is the only holder of relay secrets. It must authenticate every
    /// route with a server-side capability, store device material only as hashes, and contain
    /// no backing-store URL or key that a leak of the repository could expose.
    /// </summary>
    [Fact]
    public void GatewayWorkerKeepsRelayCredentialsServerSide()
    {
        var root = FindRepositoryRoot();
        var worker = File.ReadAllText(Path.Combine(root, "gateway", "src", "index.ts"));

        Assert.Contains("KANAL_TICKET_SECRET", worker);
        Assert.Contains("KANAL_ADMIN_TOKEN", worker);
        Assert.Contains("ticket.", worker);
        Assert.Contains("sha256Hex(deviceToken)", worker); // credentials stored hashed
        Assert.DoesNotContain("sb_publishable_", worker);
        Assert.DoesNotContain(".supabase.co", worker);
        Assert.DoesNotContain("supabase", worker, StringComparison.OrdinalIgnoreCase);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Kanal.slnx")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException("Repository root not found.");
    }

    private sealed class CaptureHandler : HttpMessageHandler
    {
        public List<(string Action, string? Bearer, string Body)> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var action = new Uri(request.RequestUri!.AbsoluteUri).Query
                .TrimStart('?').Split('&')
                .Select(value => value.Split('=', 2))
                .Single(pair => pair[0] == "action")[1];
            var body = await request.Content!.ReadAsStringAsync(cancellationToken);
            Requests.Add((action, request.Headers.Authorization?.Parameter, body));
            var response = action == "create"
                ? "{\"hostTicket\":\"host-room-ticket\",\"inviteTicket\":\"reader-ticket\"}"
                : "{}";
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(response),
            };
        }
    }
}
