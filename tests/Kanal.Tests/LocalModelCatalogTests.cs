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

    /// <summary>
    /// A reasoning model with a translation-sized token budget spends the whole budget on
    /// <c>&lt;think&gt;</c> and emits no translation at all — measured on the 2B here: 40 s per
    /// call and an empty string out of <see cref="MtOutputCleaner"/>, which is the correct
    /// reading of an unterminated think block. Prefilling a closed one turns that into 1 s and
    /// an actual sentence. Any Qwen3.x added to this catalog needs the same prefill, so the
    /// requirement is asserted on the family rather than on the two entries that exist today.
    /// </summary>
    [Fact]
    public void ReasoningModelsSuppressTheirThinkingTurn()
    {
        var reasoning = LocalModelCatalog.Models.Where(m => m.Id.StartsWith("qwen3")).ToList();
        Assert.NotEmpty(reasoning);
        foreach (var m in reasoning)
            Assert.Equal("<think>\n\n</think>\n\n", m.AssistantPrefill);
    }

    /// <summary>The prefill is a fix for reasoning models, not a thing every model wants:
    /// injected into one that does not reason, it is literal text in the translation.</summary>
    [Fact]
    public void NonReasoningModelsCarryNoPrefill()
    {
        Assert.Null(LocalModelCatalog.Find("gemma-3-4b")!.AssistantPrefill);
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

    /// <summary>
    /// A response body that hands over half the payload, signals, and then holds — so a test
    /// can act while the manager is genuinely mid-stream with its part file open.
    /// </summary>
    private sealed class GatedBody : Stream
    {
        private readonly TaskCompletionSource _started =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly Task _resume;
        private int _position;
        private bool _handedOverFirstChunk;

        public GatedBody(Task resume) => _resume = resume;

        /// <summary>Completes once the body has been read into the part file at least once.</summary>
        public Task Started => _started.Task;

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct)
        {
            if (!_handedOverFirstChunk)
            {
                _handedOverFirstChunk = true;
                var half = Math.Min(buffer.Length, Payload.Length / 2);
                Payload.AsMemory(0, half).CopyTo(buffer);
                _position = half;
                _started.TrySetResult();
                return half;
            }

            await _resume.WaitAsync(ct);
            var rest = Math.Min(buffer.Length, Payload.Length - _position);
            if (rest <= 0)
                return 0;
            Payload.AsMemory(_position, rest).CopyTo(buffer);
            _position += rest;
            return rest;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => _position; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] b, int o, int c) => throw new NotSupportedException();
        public override long Seek(long o, SeekOrigin s) => throw new NotSupportedException();
        public override void SetLength(long v) => throw new NotSupportedException();
        public override void Write(byte[] b, int o, int c) => throw new NotSupportedException();
    }

    private IEnumerable<string> PartFiles() => Directory.Exists(_dir)
        ? Directory.EnumerateFiles(_dir, "*.part")
        : Array.Empty<string>();

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
        Assert.Empty(PartFiles());
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

    /// <summary>
    /// Cancels from inside the response body, so the part file has really been created and
    /// written to before the token trips — cancelling earlier would make the manager throw out
    /// of <c>GetAsync</c> and never exercise the mid-stream cleanup this test is about.
    /// </summary>
    [Fact]
    public async Task CancelMidStreamRemovesPartialFile()
    {
        using var cts = new CancellationTokenSource();
        // never resumes: the stream parks mid-body until the token trips
        var body = new GatedBody(new TaskCompletionSource().Task);
        var handler = new FakeHandler
        {
            Respond = _ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(body),
            },
        };
        var manager = Manager(handler);
        var model = TestModel();

        var download = manager.DownloadAsync(model, null, cts.Token);
        await body.Started.WaitAsync(TimeSpan.FromSeconds(10)); // part file now exists on disk
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => download);

        Assert.False(manager.IsDownloaded(model));
        Assert.Empty(PartFiles());
    }

    /// <summary>
    /// Two downloads of the same model overlap whenever the Settings dialog is closed and
    /// reopened mid-download. The second must not touch the file the first is still streaming
    /// into — the first would otherwise die at the final rename after gigabytes of transfer.
    /// </summary>
    [Fact]
    public async Task ASecondDownloadLeavesTheFirstOnesPartFileAlone()
    {
        var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var slowBody = new GatedBody(resume.Task);
        var served = 0;
        var handler = new FakeHandler
        {
            Respond = _ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = Interlocked.Increment(ref served) == 1
                    ? new StreamContent(slowBody)
                    : new ByteArrayContent(Payload),
            },
        };
        var manager = Manager(handler);
        var model = TestModel();

        var first = manager.DownloadAsync(model, null, CancellationToken.None);
        await slowBody.Started.WaitAsync(TimeSpan.FromSeconds(10));

        // the second download runs to completion while the first still holds its part file
        await manager.DownloadAsync(model, null, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(manager.IsDownloaded(model));

        resume.SetResult();
        await first.WaitAsync(TimeSpan.FromSeconds(10)); // must not throw FileNotFoundException

        Assert.True(manager.IsDownloaded(model));
        Assert.Equal(Payload, await File.ReadAllBytesAsync(manager.GetPath(model)));
        Assert.Empty(PartFiles());
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

    /// <summary>Including the bare <c>.part</c> an older build wrote before part files
    /// became one-per-call.</summary>
    [Fact]
    public async Task DeleteSweepsLeftoverPartFiles()
    {
        var manager = Manager(new FakeHandler());
        var model = TestModel();
        await manager.DownloadAsync(model, null, CancellationToken.None);

        await File.WriteAllTextAsync(manager.GetPath(model) + ".part", "legacy");
        await File.WriteAllTextAsync(manager.GetPath(model) + ".deadbeef.part", "interrupted");

        manager.Delete(model);

        Assert.False(manager.IsDownloaded(model));
        Assert.Empty(PartFiles());
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
