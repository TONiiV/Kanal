using System.Net;
using Kanal.Core.Updates;

namespace Kanal.Core.UnitTests;

public class UpdateCheckerTests
{
    private static readonly Uri Endpoint = new("https://relay.example.test/?action=update");

    private sealed record Seen(HttpMethod Method, Uri Uri, bool HasAuthorization);

    private sealed class FakeHandler(
        Func<CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        public List<Seen> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            lock (Requests)
                Requests.Add(new Seen(
                    request.Method, request.RequestUri!, request.Headers.Authorization is not null));
            return await respond(cancellationToken);
        }
    }

    private static Task<HttpResponseMessage> Reply(HttpStatusCode status, string body) =>
        Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body) });

    private static string Manifest(string latest = "1.0.1", string minimum = "1.0.0") =>
        $$"""{"latest":"{{latest}}","minimumSupported":"{{minimum}}","notes":"Notes.","downloadUrl":null}""";

    private static async Task<(UpdateResult Result, FakeHandler Handler)> CheckAsync(
        Func<CancellationToken, Task<HttpResponseMessage>> respond,
        string version = "1.0.0",
        TimeSpan? timeout = null)
    {
        var handler = new FakeHandler(respond);
        using var http = new HttpClient(handler);
        var result = await new UpdateChecker(http, Endpoint, timeout)
            .CheckAsync(version, "windows", TestContext.Current.CancellationToken);
        return (result, handler);
    }

    private static Dictionary<string, string> Query(Uri uri) =>
        uri.Query.TrimStart('?')
            .Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(pair => pair.Split('=', 2))
            .ToDictionary(pair => pair[0], pair => Uri.UnescapeDataString(pair[1]));

    [Fact]
    public async Task AsksWithTheVersionAndThePlatformAndNothingElse()
    {
        var (_, handler) = await CheckAsync(_ => Reply(HttpStatusCode.OK, Manifest()), "1.2.3");

        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal("relay.example.test", request.Uri.Host);
        Assert.False(request.HasAuthorization);
        Assert.Equal(
            new Dictionary<string, string>
            {
                ["action"] = "update",
                ["version"] = "1.2.3",
                ["platform"] = "windows",
            },
            Query(request.Uri));
    }

    [Fact]
    public async Task ANewerVersionIsAnUpdate()
    {
        var (result, _) = await CheckAsync(_ => Reply(HttpStatusCode.OK, Manifest("1.0.1")));

        Assert.Equal(UpdateStatus.UpdateAvailable, result.Status);
        Assert.Equal(new ReleaseVersion(1, 0, 1), result.Manifest!.Latest);
        Assert.Equal("Notes.", result.Manifest.Notes);
    }

    [Fact]
    public async Task TheSameVersionIsUpToDate()
    {
        var (result, _) = await CheckAsync(_ => Reply(HttpStatusCode.OK, Manifest("1.0.0")));

        Assert.Equal(UpdateStatus.UpToDate, result.Status);
    }

    [Fact]
    public async Task AVersionBelowTheMinimumIsUnsupported()
    {
        var (result, _) = await CheckAsync(_ => Reply(HttpStatusCode.OK, Manifest("1.2.0", "1.1.0")));

        Assert.Equal(UpdateStatus.Unsupported, result.Status);
        Assert.Equal(new ReleaseVersion(1, 2, 0), result.Manifest!.Latest);
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    public async Task AnAnswerThatIsNotOkMeansUnknown(HttpStatusCode status)
    {
        var (result, _) = await CheckAsync(_ => Reply(status, Manifest("9.0.0", "9.0.0")));

        Assert.Equal(UpdateStatus.Unknown, result.Status);
        Assert.Null(result.Manifest);
    }

    [Fact]
    public async Task AnUnreachableServerMeansUnknown()
    {
        var (result, _) = await CheckAsync(
            _ => throw new HttpRequestException("name resolution failed"));

        Assert.Equal(UpdateStatus.Unknown, result.Status);
    }

    [Fact]
    public async Task ASlowServerMeansUnknownAfterTheTimeout()
    {
        var (result, _) = await CheckAsync(
            async ct =>
            {
                await Task.Delay(Timeout.Infinite, ct);
                return new HttpResponseMessage(HttpStatusCode.OK);
            },
            timeout: TimeSpan.FromMilliseconds(50));

        Assert.Equal(UpdateStatus.Unknown, result.Status);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("")]
    [InlineData("{}")]
    [InlineData("""{"latest":"soon","minimumSupported":"1.0.0"}""")]
    [InlineData("<html>captive portal</html>")]
    public async Task AMalformedBodyMeansUnknown(string body)
    {
        var (result, _) = await CheckAsync(_ => Reply(HttpStatusCode.OK, body));

        Assert.Equal(UpdateStatus.Unknown, result.Status);
        Assert.Null(result.Manifest);
    }

    [Theory]
    [InlineData("dev")]
    [InlineData("")]
    [InlineData("1.0.0-beta")]
    public async Task ACurrentVersionThatDoesNotParseMeansUnknown(string version)
    {
        var (result, _) = await CheckAsync(
            _ => Reply(HttpStatusCode.OK, Manifest("9.0.0", "9.0.0")), version);

        Assert.Equal(UpdateStatus.Unknown, result.Status);
    }

    [Fact]
    public void ThePlatformNameIsOneOfTheThreeTheServerKnows()
    {
        Assert.Contains(UpdatePlatform.Current, new[] { "windows", "macos", "linux" });
    }

    [Fact]
    public void TheProductionAddressIsTheRelayUpdateAction()
    {
        Assert.Equal(
            "https://kanal-relay.toniiv.workers.dev/?action=update",
            UpdateChecker.ProductionUrl);
    }
}
