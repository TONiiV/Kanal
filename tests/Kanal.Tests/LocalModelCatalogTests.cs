using System.Security.Cryptography;
using System.Net;
using System.Text;
using Kanal.Providers.LocalMt;

namespace Kanal.Tests;

public class LocalModelCatalogTests
{
    [Fact]
    public void PreferredModelIsQwen35_4B()
    {
        var first = LocalModelCatalog.Models[0];
        Assert.Equal("qwen3.5-4b", first.Id);
        Assert.Equal("Apache-2.0", first.License);
        Assert.Null(first.LicenseNote); // Apache needs no warning
    }

    [Fact]
    public void EveryEntryIsComplete()
    {
        Assert.InRange(LocalModelCatalog.Models.Count, 3, 5);
        foreach (var m in LocalModelCatalog.Models)
        {
            Assert.False(string.IsNullOrWhiteSpace(m.Id));
            Assert.False(string.IsNullOrWhiteSpace(m.DisplayName));
            Assert.False(string.IsNullOrWhiteSpace(m.Parameters));
            Assert.Matches("^[0-9a-f]{64}$", m.Sha256);
            Assert.True(m.SizeBytes > 500_000_000, $"{m.Id} size looks wrong");
            Assert.EndsWith(".gguf", m.FileName);
            Assert.StartsWith("https://huggingface.co/", m.DownloadUrl);
            Assert.EndsWith(m.FileName, m.DownloadUrl);
            Assert.False(string.IsNullOrWhiteSpace(m.License));
        }
    }

    [Fact]
    public void NonPermissiveLicensesCarryANote()
    {
        foreach (var m in LocalModelCatalog.Models)
        {
            var permissive = m.License is "Apache-2.0" or "MIT";
            if (!permissive)
                Assert.False(string.IsNullOrWhiteSpace(m.LicenseNote),
                    $"{m.Id} has license {m.License} and must carry a LicenseNote");
        }
    }

    [Fact]
    public void FindResolvesByIdAndToleratesUnknown()
    {
        Assert.NotNull(LocalModelCatalog.Find("qwen3.5-4b"));
        Assert.Null(LocalModelCatalog.Find("no-such-model"));
        Assert.Null(LocalModelCatalog.Find(null));
    }

    [Fact]
    public void SizeLabelIsHumanReadable()
    {
        var m = LocalModelCatalog.Models[0];
        Assert.Contains("GB", m.SizeLabel);
    }
}

public class ModelDownloadManagerTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "kanal-tests", Guid.NewGuid().ToString("N"));

    private static readonly byte[] Payload = Encoding.UTF8.GetBytes("fake gguf payload KX-4402");

    private sealed class FakeHandler : HttpMessageHandler
    {
        public Func<HttpRequestMessage, HttpResponseMessage> Respond { get; set; } =
            _ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(Payload),
            };

        public Uri? LastUri;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct)
        {
            LastUri = request.RequestUri;
            return Task.FromResult(Respond(request));
        }
    }

    private static LocalModelInfo TestModel(string? sha = null) => new(
        Id: "test-model",
        DisplayName: "Test Model",
        Parameters: "4B",
        Repo: "example/test-GGUF",
        FileName: "test-Q4_K_M.gguf",
        SizeBytes: Payload.Length,
        Sha256: sha ?? Convert.ToHexStringLower(SHA256.HashData(Payload)),
        License: "Apache-2.0");

    private ModelDownloadManager Manager(FakeHandler handler) =>
        new(_dir, new HttpClient(handler));

    [Fact]
    public async Task DownloadsVerifiesAndReportsProgress()
    {
        var handler = new FakeHandler();
        var manager = Manager(handler);
        var model = TestModel();
        var progress = new List<double>();

        Assert.False(manager.IsDownloaded(model));
        await manager.DownloadAsync(model, new Progress<double>(progress.Add), CancellationToken.None);

        Assert.True(manager.IsDownloaded(model));
        Assert.Equal(Payload, await File.ReadAllBytesAsync(manager.GetPath(model)));
        Assert.Equal(model.DownloadUrl, handler.LastUri!.ToString());
        // progress lands via a SynchronizationContext-free Progress<>, give it a beat
        await Task.Delay(50);
        Assert.Contains(progress, p => p >= 1.0);
    }

    [Fact]
    public async Task Sha256MismatchThrowsAndLeavesNoFile()
    {
        var manager = Manager(new FakeHandler());
        var model = TestModel(sha: new string('0', 64));

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            manager.DownloadAsync(model, null, CancellationToken.None));

        Assert.False(manager.IsDownloaded(model));
        Assert.False(File.Exists(manager.GetPath(model) + ".part"));
    }

    [Fact]
    public async Task HttpErrorThrowsAndLeavesNoFile()
    {
        var handler = new FakeHandler
        {
            Respond = _ => new HttpResponseMessage(HttpStatusCode.NotFound),
        };
        var manager = Manager(handler);

        await Assert.ThrowsAsync<HttpRequestException>(() =>
            manager.DownloadAsync(TestModel(), null, CancellationToken.None));

        Assert.False(manager.IsDownloaded(TestModel()));
    }

    [Fact]
    public async Task CancelRemovesPartialFile()
    {
        using var cts = new CancellationTokenSource();
        var handler = new FakeHandler
        {
            Respond = _ =>
            {
                cts.Cancel(); // cancel while the body is being streamed
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(Payload),
                };
            },
        };
        var manager = Manager(handler);
        var model = TestModel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            manager.DownloadAsync(model, null, cts.Token));

        Assert.False(manager.IsDownloaded(model));
        Assert.False(File.Exists(manager.GetPath(model) + ".part"));
    }

    [Fact]
    public async Task DeleteRemovesDownloadedModel()
    {
        var manager = Manager(new FakeHandler());
        var model = TestModel();
        await manager.DownloadAsync(model, null, CancellationToken.None);
        Assert.True(manager.IsDownloaded(model));

        manager.Delete(model);

        Assert.False(manager.IsDownloaded(model));
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
