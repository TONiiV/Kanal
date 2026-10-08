using System.Net;
using System.Security.Cryptography;
using System.Text;
using Kanal.Core.Models;

namespace Kanal.Core.UnitTests;

public class DiarizationReadinessTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "kanal-diar", Guid.NewGuid().ToString("N"));

    private static readonly byte[] Payload = Encoding.UTF8.GetBytes("fake onnx payload");

    private sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public Uri? LastUri;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            LastUri = request.RequestUri;
            return Task.FromResult(respond(request));
        }
    }

    private static HttpResponseMessage Ok(HttpRequestMessage _) =>
        new(HttpStatusCode.OK) { Content = new ByteArrayContent(Payload) };

    private ModelDownloadManager Manager(Func<HttpRequestMessage, HttpResponseMessage>? respond = null) =>
        new(_dir, new HttpClient(new FakeHandler(respond ?? Ok)));

    private static DiarizationModelInfo TestModel(DiarizationModelRole role, string id) => new(
        Id: id,
        DisplayName: id,
        Role: role,
        ReleaseTag: "speaker-recongition-models",
        FileName: id + ".onnx",
        SizeBytes: Payload.Length,
        Sha256: Convert.ToHexStringLower(SHA256.HashData(Payload)),
        License: ModelLicense.Apache20);

    private void Place(DiarizationModelInfo model)
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllBytes(Path.Combine(_dir, model.FileName), Payload);
    }

    [Fact]
    public void AttributionIsUnavailableUntilBothModelsAreOnDisk()
    {
        var downloads = Manager();
        var segmentation = DiarizationModelCatalog.Segmentation[0];
        var embedding = DiarizationModelCatalog.Embeddings[0];

        Assert.Equal(DiarizationReadiness.NotDownloaded,
            DiarizationModelCatalog.Readiness(downloads, segmentation.Id, embedding.Id));

        Place(segmentation);
        Assert.Equal(DiarizationReadiness.NotDownloaded,
            DiarizationModelCatalog.Readiness(downloads, segmentation.Id, embedding.Id));

        Place(embedding);
        Assert.Equal(DiarizationReadiness.Ready,
            DiarizationModelCatalog.Readiness(downloads, segmentation.Id, embedding.Id));
    }

    [Fact]
    public void NoChoiceOrAnUnknownChoiceIsSimplyNotReady()
    {
        var downloads = Manager();

        Assert.Equal(DiarizationReadiness.NotDownloaded,
            DiarizationModelCatalog.Readiness(downloads, null, null));
        Assert.Equal(DiarizationReadiness.NotDownloaded,
            DiarizationModelCatalog.Readiness(downloads, "reverb-diarization-v1", "no-such-model"));
    }

    [Fact]
    public void TheTwoChoicesMustBeOneOfEachRole()
    {
        var downloads = Manager();
        var segmentation = DiarizationModelCatalog.Segmentation[0];
        Place(segmentation);

        Assert.Equal(DiarizationReadiness.NotDownloaded,
            DiarizationModelCatalog.Readiness(downloads, segmentation.Id, segmentation.Id));
    }

    [Fact]
    public async Task ADownloadedModelBecomesReady()
    {
        var model = TestModel(DiarizationModelRole.Embedding, "test-embedding");
        var download = new DiarizationModelDownload(model, Manager());

        Assert.Equal(DiarizationReadiness.NotDownloaded, download.State);

        await download.DownloadAsync();

        Assert.Equal(DiarizationReadiness.Ready, download.State);
        Assert.Equal(1.0, download.Progress);
        Assert.Equal("", download.Error);
    }

    [Fact]
    public async Task AFailedDownloadIsAStateRatherThanAnException()
    {
        var model = TestModel(DiarizationModelRole.Embedding, "test-embedding");
        var download = new DiarizationModelDownload(model, Manager(_ => new HttpResponseMessage(HttpStatusCode.NotFound)));

        await download.DownloadAsync();

        Assert.Equal(DiarizationReadiness.Failed, download.State);
        Assert.NotEqual("", download.Error);
    }

    [Fact]
    public async Task ACorruptedDownloadFailsRatherThanBecomingReady()
    {
        var model = TestModel(DiarizationModelRole.Embedding, "test-embedding") with { Sha256 = new string('0', 64) };
        var downloads = Manager();
        var download = new DiarizationModelDownload(model, downloads);

        await download.DownloadAsync();

        Assert.Equal(DiarizationReadiness.Failed, download.State);
        Assert.False(downloads.IsDownloaded(model));
    }

    [Fact]
    public async Task CancellingLeavesNothingBehindAndNoError()
    {
        var model = TestModel(DiarizationModelRole.Embedding, "test-embedding");
        var download = new DiarizationModelDownload(model, Manager());
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await download.DownloadAsync(cts.Token);

        Assert.Equal(DiarizationReadiness.NotDownloaded, download.State);
        Assert.Equal("", download.Error);
        Assert.False(Directory.Exists(_dir) && Directory.EnumerateFileSystemEntries(_dir).Any());
    }

    [Fact]
    public async Task DeletingReturnsToNotDownloaded()
    {
        var model = TestModel(DiarizationModelRole.Embedding, "test-embedding");
        var download = new DiarizationModelDownload(model, Manager());
        await download.DownloadAsync();

        download.Delete();

        Assert.Equal(DiarizationReadiness.NotDownloaded, download.State);
        Assert.False(File.Exists(Path.Combine(_dir, model.FileName)));
    }

    [Fact]
    public void AnAlreadyDownloadedModelStartsReady()
    {
        var model = TestModel(DiarizationModelRole.Embedding, "test-embedding");
        Place(model);

        Assert.Equal(DiarizationReadiness.Ready, new DiarizationModelDownload(model, Manager()).State);
    }

    [Fact]
    public async Task TheCatalogUrlReachesTheDownloaderVerbatim()
    {
        var handler = new FakeHandler(Ok);
        var downloads = new ModelDownloadManager(_dir, new HttpClient(handler));
        var model = TestModel(DiarizationModelRole.Embedding, "test-embedding");

        await new DiarizationModelDownload(model, downloads).DownloadAsync();

        Assert.Equal(model.DownloadUrl, handler.LastUri!.ToString());
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_dir))
                Directory.Delete(_dir, recursive: true);
        }
        catch
        {
        }
    }
}
