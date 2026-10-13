using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Kanal.Core.Models;
using Kanal.Core.Providers;
using Kanal.Providers.DeepL;

namespace Kanal.Core.UnitTests;

public class DeepLMtProviderTests
{
    private sealed record Seen(
        string Method, string Host, string Path, string? ContentType, string? Auth, JsonNode Body);

    private sealed class FakeHandler(Func<Seen, CancellationToken, Task<HttpResponseMessage>>? respond = null)
        : HttpMessageHandler
    {
        public List<Seen> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync(ct))!;
            var seen = new Seen(
                request.Method.Method,
                request.RequestUri!.Host,
                request.RequestUri.AbsolutePath,
                request.Content.Headers.ContentType?.MediaType,
                request.Headers.Authorization?.ToString(),
                body);
            lock (Requests)
                Requests.Add(seen);
            return await (respond ?? Echo)(seen, ct);
        }

        public static Task<HttpResponseMessage> Echo(Seen seen, CancellationToken ct) =>
            Task.FromResult(Json(HttpStatusCode.OK, new JsonObject
            {
                ["translations"] = new JsonArray(new JsonObject
                {
                    ["detected_source_language"] = seen.Body["source_lang"]!.GetValue<string>(),
                    ["text"] = seen.Body["target_lang"]!.GetValue<string>() + ":" + seen.Body["text"]![0]!.GetValue<string>(),
                }),
            }));
    }

    private static HttpResponseMessage Json(HttpStatusCode status, JsonNode body) =>
        new(status) { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") };

    private static Task<IReadOnlyDictionary<string, string>> Translate(
        DeepLMtProvider provider, string from, string[] to, IReadOnlyList<Utterance>? context = null,
        CancellationToken ct = default) =>
        provider.TranslateAsync("hello", from, to, context ?? [], ct);

    private static Utterance Final(string id, string text) => new(
        id, "S01", 0, 900, "zh", text, 1, UtteranceState.Final, false, 0.9, new Dictionary<string, string>());

    [Theory]
    [InlineData("abc:fx", "api-free.deepl.com")]
    [InlineData("abc", "api.deepl.com")]
    public async Task TheKeySuffixChoosesTheHost(string key, string host)
    {
        var handler = new FakeHandler();
        var provider = new DeepLMtProvider(key, new HttpClient(handler));

        await Translate(provider, "zh", ["de"], ct: TestContext.Current.CancellationToken);

        var seen = Assert.Single(handler.Requests);
        Assert.Equal(host, seen.Host);
        Assert.Equal("POST", seen.Method);
        Assert.Equal("/v2/translate", seen.Path);
        Assert.Equal("application/json", seen.ContentType);
        Assert.Equal($"DeepL-Auth-Key {key}", seen.Auth);
    }

    [Fact]
    public async Task RoomCodesMapToDeepLCodesAndBackToRoomCodes()
    {
        var handler = new FakeHandler();
        var provider = new DeepLMtProvider("k", new HttpClient(handler));

        var result = await Translate(provider, "zh", ["de", "pl", "en"], ct: TestContext.Current.CancellationToken);

        Assert.All(handler.Requests, r => Assert.Equal("ZH", r.Body["source_lang"]!.GetValue<string>()));
        Assert.Equal(
            ["DE", "EN-GB", "PL"],
            handler.Requests.Select(r => r.Body["target_lang"]!.GetValue<string>()).Order());
        Assert.Equal("DE:hello", result["de"]);
        Assert.Equal("PL:hello", result["pl"]);
        Assert.Equal("EN-GB:hello", result["en"]);

        handler.Requests.Clear();
        var toChinese = await Translate(provider, "en", ["zh"], ct: TestContext.Current.CancellationToken);

        var seen = Assert.Single(handler.Requests);
        Assert.Equal("EN", seen.Body["source_lang"]!.GetValue<string>());
        Assert.Equal("ZH-HANS", seen.Body["target_lang"]!.GetValue<string>());
        Assert.Equal("ZH-HANS:hello", toChinese["zh"]);
    }

    [Fact]
    public async Task TargetsRunInParallel()
    {
        var arrived = 0;
        var both = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new FakeHandler(async (seen, ct) =>
        {
            if (Interlocked.Increment(ref arrived) == 2)
                both.SetResult();
            await both.Task;
            return await FakeHandler.Echo(seen, ct);
        });
        var provider = new DeepLMtProvider("k", new HttpClient(handler));

        var result = await Translate(provider, "zh", ["de", "pl"], ct: TestContext.Current.CancellationToken)
            .WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.Equal(2, result.Count);
    }

    [Fact]
    public async Task AnUnsupportedTargetIsSkippedWithoutARequestOrAnError()
    {
        var handler = new FakeHandler();
        var provider = new DeepLMtProvider("k", new HttpClient(handler));

        var result = await Translate(provider, "zh", ["de", "xx"], ct: TestContext.Current.CancellationToken);

        Assert.Equal(["de"], result.Keys);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task TheSourceLanguageIsNotATarget()
    {
        var handler = new FakeHandler();
        var provider = new DeepLMtProvider("k", new HttpClient(handler));

        var result = await Translate(provider, "zh", ["zh", "de"], ct: TestContext.Current.CancellationToken);

        Assert.Equal(["de"], result.Keys);
        Assert.Single(handler.Requests);
    }

    [Theory]
    [InlineData(403, MtProviderException.InvalidKey)]
    [InlineData(456, MtProviderException.QuotaExceeded)]
    [InlineData(429, MtProviderException.Unavailable)]
    [InlineData(500, MtProviderException.Unavailable)]
    [InlineData(503, MtProviderException.Unavailable)]
    [InlineData(400, null)]
    public async Task AFailedRequestThrowsWithTheCodeTheHostLocalises(int status, string? code)
    {
        var handler = new FakeHandler((_, _) => Task.FromResult(Json((HttpStatusCode)status, new JsonObject())));
        var provider = new DeepLMtProvider("k", new HttpClient(handler));

        var ex = await Assert.ThrowsAsync<MtProviderException>(
            () => Translate(provider, "zh", ["de"], ct: TestContext.Current.CancellationToken));

        Assert.Equal(code, ex.Code);
    }

    [Theory]
    [InlineData(403)]
    [InlineData(456)]
    public async Task ARefusedKeyOrQuotaStopsAllFurtherRequests(int status)
    {
        var handler = new FakeHandler((_, _) => Task.FromResult(Json((HttpStatusCode)status, new JsonObject())));
        var provider = new DeepLMtProvider("k", new HttpClient(handler));

        await Assert.ThrowsAsync<MtProviderException>(
            () => Translate(provider, "zh", ["de"], ct: TestContext.Current.CancellationToken));
        var later = await Translate(provider, "zh", ["de", "pl"], ct: TestContext.Current.CancellationToken);

        Assert.Empty(later);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task OverlappingRefusalsRaiseOnlyOneError()
    {
        var arrived = 0;
        var both = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new FakeHandler(async (_, _) =>
        {
            if (Interlocked.Increment(ref arrived) == 2)
                both.SetResult();
            await both.Task;
            return Json(HttpStatusCode.Forbidden, new JsonObject());
        });
        var provider = new DeepLMtProvider("k", new HttpClient(handler));

        var calls = new[]
        {
            Translate(provider, "zh", ["de"], ct: TestContext.Current.CancellationToken),
            Translate(provider, "zh", ["pl"], ct: TestContext.Current.CancellationToken),
        };
        await Task.WhenAny(Task.WhenAll(calls.Select(c => c.ContinueWith(_ => { }))))
            .WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.Equal(1, calls.Count(c => c.IsFaulted));
        Assert.Equal(1, calls.Count(c => c.IsCompletedSuccessfully && c.Result.Count == 0));
    }

    [Fact]
    public async Task AnUnavailableServiceIsRetriedOnTheNextCall()
    {
        var handler = new FakeHandler((_, _) => Task.FromResult(Json(HttpStatusCode.ServiceUnavailable, new JsonObject())));
        var provider = new DeepLMtProvider("k", new HttpClient(handler));

        for (var i = 0; i < 2; i++)
            await Assert.ThrowsAsync<MtProviderException>(
                () => Translate(provider, "zh", ["de"], ct: TestContext.Current.CancellationToken));

        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task ATimeoutIsReportedNotSwallowedAsACancellation()
    {
        var handler = new FakeHandler(async (_, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return null!;
        });
        var provider = new DeepLMtProvider("k", new HttpClient(handler) { Timeout = TimeSpan.FromMilliseconds(50) });

        var ex = await Assert.ThrowsAsync<MtProviderException>(
            () => Translate(provider, "zh", ["de"], ct: TestContext.Current.CancellationToken));

        Assert.Equal(MtProviderException.Unavailable, ex.Code);
    }

    [Fact]
    public async Task ANetworkErrorIsReportedAsUnavailable()
    {
        var handler = new FakeHandler((_, _) => throw new HttpRequestException("no route"));
        var provider = new DeepLMtProvider("k", new HttpClient(handler));

        var ex = await Assert.ThrowsAsync<MtProviderException>(
            () => Translate(provider, "zh", ["de"], ct: TestContext.Current.CancellationToken));

        Assert.Equal(MtProviderException.Unavailable, ex.Code);
    }

    [Fact]
    public async Task RecentFinalsGoInTheContextParameter()
    {
        var handler = new FakeHandler();
        var provider = new DeepLMtProvider("k", new HttpClient(handler));

        await Translate(
            provider, "zh", ["de"], [Final("u1", "料号 KX-4402"), Final("u2", "首件检验")],
            TestContext.Current.CancellationToken);
        await Translate(provider, "zh", ["de"], ct: TestContext.Current.CancellationToken);

        var context = handler.Requests[0].Body["context"]!.GetValue<string>();
        Assert.Contains("料号 KX-4402", context);
        Assert.Contains("首件检验", context);
        Assert.Null(handler.Requests[1].Body["context"]);
    }

    [Fact]
    public async Task CancellationReachesTheRequest()
    {
        var handler = new FakeHandler(async (_, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return null!;
        });
        var provider = new DeepLMtProvider("k", new HttpClient(handler));
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cts.CancelAfter(50);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => Translate(provider, "zh", ["de"], ct: cts.Token));
    }
}
