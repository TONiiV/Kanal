using System.Net;
using Kanal.Core.Relay;

namespace Kanal.Core.UnitTests;

public class RelayVersionHeaderTests
{
    private sealed class FakeHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public List<string[]> VersionHeaders { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            VersionHeaders.Add(request.Headers.TryGetValues("X-Kanal-Version", out var values)
                ? values.ToArray()
                : []);
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body) });
        }
    }

    private const string Created = """{"hostTicket":"host-room-ticket","inviteTicket":"reader-ticket"}""";

    private static Task<GatewayRoom> CreateAsync(FakeHandler handler, string version = "1.2.3")
    {
        var http = new HttpClient(handler);
        return GatewayRelayPublisher.CreateRoomAsync(
            "https://relay.example.test/kanal-relay",
            "host-bootstrap-token-with-32-bytes",
            "room-capability",
            "verification-key",
            version,
            http,
            TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task CreatingARoomSendsTheHostVersion()
    {
        var handler = new FakeHandler(HttpStatusCode.OK, Created);

        await CreateAsync(handler, "1.2.3");

        Assert.Equal("1.2.3", Assert.Single(Assert.Single(handler.VersionHeaders)));
    }

    [Fact]
    public async Task AnUpgradeRequiredAnswerIsToldApartFromOtherFailures()
    {
        var handler = new FakeHandler(
            HttpStatusCode.UpgradeRequired, """{"error":"minimum_version","minimumSupported":"1.1.0"}""");

        var error = await Assert.ThrowsAsync<RelayVersionRejectedException>(() => CreateAsync(handler));

        Assert.IsAssignableFrom<InvalidOperationException>(error);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task OtherFailuresStayPlainFailures(HttpStatusCode status)
    {
        var handler = new FakeHandler(status, "{}");

        var error = await Assert.ThrowsAnyAsync<InvalidOperationException>(() => CreateAsync(handler));

        Assert.IsNotType<RelayVersionRejectedException>(error);
    }
}
