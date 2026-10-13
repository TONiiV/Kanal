using System.Net;
using Avalonia.Headless.XUnit;
using Kanal.Host.Services;
using Kanal.Host.ViewModels;
using Kanal.Core.Models;
using Kanal.Providers.LocalAsr;
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

    private static string NewDir() =>
        Path.Combine(Path.GetTempPath(), "kanal-tests", Guid.NewGuid().ToString("N"));

    private static void TryDelete(string dir)
    {
        try
        {
            Directory.Delete(dir, recursive: true);
        }
        catch
        {
        }
    }

    [AvaloniaFact]
    public async Task ClosingSettingsKeepsADownloadRunning()
    {
        var dir = NewDir();
        var handler = new StalledHandler();
        var rows = new ModelRows(new ModelDownloadManager(dir, new HttpClient(handler)));
        var vm = new SettingsViewModel(new AppSettings(), () => null, modelRows: rows);
        var item = vm.TranslationModels.First(m => m.IsLocal);

        var download = item.DownloadCommand.ExecuteAsync(null);
        await handler.Started.WaitAsync(TimeSpan.FromSeconds(10));

        vm.OnWindowClosed();

        Assert.True(item.IsDownloading);
        Assert.False(download.IsCompleted);

        rows.CancelAll();
        await download.WaitAsync(TimeSpan.FromSeconds(10));
        TryDelete(dir);
    }

    [AvaloniaFact]
    public async Task ReopeningSettingsShowsTheRunningDownloadWithAWorkingCancel()
    {
        var dir = NewDir();
        var handler = new StalledHandler();
        var rows = new ModelRows(new ModelDownloadManager(dir, new HttpClient(handler)));
        var first = new SettingsViewModel(new AppSettings(), () => null, modelRows: rows);
        var item = first.TranscriptionModels[0];

        var download = item.DownloadCommand.ExecuteAsync(null);
        await handler.Started.WaitAsync(TimeSpan.FromSeconds(10));
        item.Progress = 0.4;
        first.OnWindowClosed();

        var second = new SettingsViewModel(new AppSettings(), () => null, modelRows: rows);
        var reopened = second.TranscriptionModels[0];

        Assert.Same(item, reopened);
        Assert.True(reopened.IsDownloading);
        Assert.Equal(0.4, reopened.Progress);

        reopened.CancelDownloadCommand.Execute(null);

        await download.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.False(reopened.IsDownloading);
        Assert.Equal("", reopened.Error);
        TryDelete(dir);
    }

    [AvaloniaFact]
    public async Task DisposingTheMainViewModelCancelsEveryRunningDownload()
    {
        var dir = NewDir();
        var handler = new StalledHandler();
        var vm = TestViewModels.Hermetic(
            downloads: new ModelDownloadManager(dir, new HttpClient(handler)));
        var translation = vm.ModelRows.Translation.First(m => m.IsLocal);

        var download = translation.DownloadCommand.ExecuteAsync(null);
        await handler.Started.WaitAsync(TimeSpan.FromSeconds(10));

        vm.Dispose();

        await download.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.False(translation.IsDownloading);
        Assert.Equal("", translation.Error);
        TryDelete(dir);
    }
}
