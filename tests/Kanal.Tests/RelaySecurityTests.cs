using System.Net;
using System.Text.Json;
using Kanal.Core.Relay;
using Kanal.Host.Services;

namespace Kanal.Tests;

public class RelaySecurityTests
{
    [Fact]
    public void JoinUrlCarriesOnlyTheRoomCapabilityAndVerificationKey()
    {
        var settings = new RelaySettings(
            "https://secret-project.supabase.co",
            "sb_publishable_do-not-put-this-in-the-url",
            "https://example.test/mobile/");

        var url = settings.BuildJoinUrl("kanal-room-capability", "public-verification-key");

        Assert.Equal(
            "https://example.test/mobile/#room=kanal-room-capability&vk=public-verification-key",
            url);
        Assert.DoesNotContain("supabase", url, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("publishable", url, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("sbref", url, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("key=", url, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ShippedCredentialIsAModernPublishableKey()
    {
        Assert.StartsWith("sb_publishable_", RelaySettings.DefaultPublishableKey);
        Assert.DoesNotContain("eyJ", RelaySettings.DefaultPublishableKey);
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
    public async Task SupabaseGetsASignedEnvelopeAndNoBearerCredential()
    {
        var handler = new CaptureHandler();
        using var http = new HttpClient(handler);
        await using var transport = new SupabaseRelayPublisher(
            "https://project.supabase.co", "sb_publishable_test", "room", http);
        using var key = RelaySigningKey.Create();
        await using var signed = new SignedRelayPublisher(transport, key);

        await signed.PublishAsync(new RoomRecordingMessage(true));

        Assert.NotNull(handler.Request);
        Assert.Equal("sb_publishable_test", handler.Request!.Headers.GetValues("apikey").Single());
        Assert.Null(handler.Request.Headers.Authorization);

        using var body = JsonDocument.Parse(handler.Body!);
        var payload = body.RootElement.GetProperty("messages")[0].GetProperty("payload");
        var envelope = Assert.IsType<SignedRelayMessage>(
            RelayJson.Deserialize(payload.GetRawText()));
        Assert.True(RelaySigningKey.TryVerify(key.VerificationKey, envelope, out var message));
        Assert.True(Assert.IsType<RoomRecordingMessage>(message).Recording);
    }

    [Fact]
    public void MobilePageVerifiesBeforeDispatchAndShipsNoLegacyKey()
    {
        var root = FindRepositoryRoot();
        var web = File.ReadAllText(Path.Combine(root, "web", "index.html"));
        var docs = File.ReadAllText(Path.Combine(root, "docs", "index.html"));

        Assert.Equal(web, docs);
        Assert.Contains("crypto.subtle.verify", web);
        Assert.Contains("await verifyEnvelope", web);
        Assert.DoesNotContain("params.get(\"key\")", web);
        Assert.DoesNotContain("params.get(\"sbref\")", web);
        Assert.DoesNotContain("eyJhbGciOiJIUzI1Ni", web);
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
        public HttpRequestMessage? Request { get; private set; }
        public string? Body { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Request = request;
            Body = await request.Content!.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }
}
