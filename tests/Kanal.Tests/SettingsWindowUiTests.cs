using System.Net;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Kanal.Host.Services;
using Kanal.Host.ViewModels;
using Kanal.Host.Views;
using Kanal.Providers.LocalMt;

namespace Kanal.Tests;

public class SettingsWindowUiTests
{
    [AvaloniaFact]
    public void TranslationModelSectionListsCloudAndCatalog()
    {
        var window = new SettingsWindow
        {
            DataContext = new SettingsViewModel(new AppSettings()),
        };
        window.Show();

        var texts = window.GetVisualDescendants().OfType<TextBlock>()
            .Select(t => t.Text).Where(t => t is not null).ToList();
        Assert.Contains("TRANSLATION MODEL", texts);
        Assert.Contains("Gladia cloud", texts);
        foreach (var model in LocalModelCatalog.Models)
            Assert.Contains(model.DisplayName, texts);

        // one radio per choice, in the shared activeModel group
        var radios = window.GetVisualDescendants().OfType<RadioButton>()
            .Where(r => r.GroupName == "activeModel").ToList();
        Assert.Equal(LocalModelCatalog.Models.Count + 1, radios.Count);
        Assert.Equal(1, radios.Count(r => r.IsChecked == true));

        window.Close();
    }

    [AvaloniaFact]
    public void DownloadButtonsShowOnlyForLocalModelsNotYetDownloaded()
    {
        var window = new SettingsWindow
        {
            DataContext = new SettingsViewModel(new AppSettings()),
        };
        window.Show();

        var downloadButtons = window.GetVisualDescendants().OfType<Button>()
            .Where(b => Equals(b.Content, "Download") && b.IsVisible).ToList();
        // the cloud row never offers a download
        Assert.InRange(downloadButtons.Count, 0, LocalModelCatalog.Models.Count);
        Assert.All(downloadButtons, b =>
            Assert.True(((TranslationModelItemViewModel)b.DataContext!).IsLocal));

        window.Close();
    }

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
    /// Closing the dialog mid-download used to orphan the running task: MainWindow builds a
    /// fresh SettingsWindow and SettingsViewModel every time it opens, so reopening offered
    /// Download again while the first transfer was still going — two downloads of one model.
    /// </summary>
    [AvaloniaFact]
    public async Task ClosingTheDialogCancelsARunningDownload()
    {
        var dir = Path.Combine(Path.GetTempPath(), "kanal-tests", Guid.NewGuid().ToString("N"));
        var handler = new StalledHandler();
        var item = new TranslationModelItemViewModel(
            LocalModelCatalog.Models[0], new ModelDownloadManager(dir, new HttpClient(handler)));

        var vm = new SettingsViewModel(new AppSettings());
        vm.TranslationModels.Clear();
        vm.TranslationModels.Add(item);

        var window = new SettingsWindow { DataContext = vm };
        window.Show();

        var download = item.DownloadCommand.ExecuteAsync(null);
        await handler.Started.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(item.IsDownloading);

        window.Close();

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
