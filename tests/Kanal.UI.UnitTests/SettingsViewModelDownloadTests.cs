using System.Net;
using Avalonia.Headless.XUnit;
using Kanal.Host.Services;
using Kanal.Host.ViewModels;
using Kanal.Providers.LocalMt;

namespace Kanal.UI.UnitTests;

public class SettingsViewModelDownloadTests
{
    /// <summary>A response body that never yields until the request is cancelled.</summary>
    private sealed class StalledHandler : HttpMessageHandler
    {
        private readonly TaskCompletionSource _started =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Started => _started.Task;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct)
        {
            _started.TrySetResult();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(new StalledBody()),
            });
        }

        private sealed class StalledBody : Stream
        {
            public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct)
            {
                await new TaskCompletionSource().Task.WaitAsync(ct);
                return 0;
            }

            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => throw new NotSupportedException();
            public override long Position { get => 0; set => throw new NotSupportedException(); }
            public override void Flush() { }
            public override int Read(byte[] b, int o, int c) => throw new NotSupportedException();
            public override long Seek(long o, SeekOrigin s) => throw new NotSupportedException();
            public override void SetLength(long v) => throw new NotSupportedException();
            public override void Write(byte[] b, int o, int c) => throw new NotSupportedException();
        }
    }

    /// <summary>
    /// Disposing the settings state mid-download must cancel its running work so reopening the
    /// settings surface cannot offer a second concurrent download of the same model.
    /// </summary>
    [AvaloniaFact]
    public async Task CancellingSettingsStateStopsARunningDownload()
    {
        var dir = Path.Combine(Path.GetTempPath(), "kanal-tests", Guid.NewGuid().ToString("N"));
        var handler = new StalledHandler();
        var item = new TranslationModelItemViewModel(
            LocalModelCatalog.Models[0], new ModelDownloadManager(dir, new HttpClient(handler)));

        var vm = new SettingsViewModel(new AppSettings());
        vm.TranslationModels.Clear();
        vm.TranslationModels.Add(item);

        var download = item.DownloadCommand.ExecuteAsync(null);
        await handler.Started.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(item.IsDownloading);

        vm.CancelDownloads();

        await download.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.False(item.IsDownloading);
        Assert.Equal("", item.Error); // a cancelled download is not a failure
        Assert.False(Directory.Exists(dir) && Directory.EnumerateFiles(dir).Any());

        try
        {
            Directory.Delete(dir, recursive: true);
        }
        catch
        {
        }
    }
}
