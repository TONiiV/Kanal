using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Kanal.Core.Models;
using Kanal.Host.Services;
using Kanal.Host.ViewModels;

namespace Kanal.UI.UnitTests;

/// <summary>
/// No diarization model on disk means speaker attribution is unavailable, and unavailable has
/// to mean nothing at all happens: the meeting starts, the transcript runs, and the operator is
/// not told about a model they did not ask for. A blocking reason added here later fails these.
/// </summary>
public class SpeakerAttributionDegradationTests
{
    private static async Task WaitForAsync(Func<bool> condition, int timeoutMs = 15_000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (!condition())
        {
            if (Environment.TickCount64 > deadline)
                throw new TimeoutException("Condition not met in time.");
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(50);
        }

        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public void NoDiarizationModelIsNotAReasonAPipelineCannotRun()
    {
        var dir = TestViewModels.EmptyModelsDir();
        Assert.Equal(
            DiarizationReadiness.NotDownloaded,
            DiarizationModelCatalog.Readiness(
                new ModelDownloadManager(dir),
                DiarizationModelCatalog.Segmentation[0].Id,
                DiarizationModelCatalog.Embeddings[0].Id));

        var vm = TestViewModels.Demo(modelsDir: dir);

        Assert.True(vm.Modes.First(o => o.Mode.Id == PipelineModeId.Demo).IsAvailable);
        foreach (var option in vm.Modes)
        foreach (var word in new[] { "speaker", "diariz", "segmentation", "embedding" })
            Assert.DoesNotContain(word, option.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [AvaloniaFact]
    public async Task AMeetingRunsToTranscriptWithNoDiarizationModelDownloaded()
    {
        var vm = TestViewModels.Demo(modelsDir: TestViewModels.EmptyModelsDir());

        await vm.StartCommand.ExecuteAsync(null);
        await WaitForAsync(() => vm.Columns.Any(c => c.Bubbles.Count > 0));

        Assert.True(vm.IsRunning);
        foreach (var word in new[] { "speaker", "diariz", "segmentation", "embedding" })
            Assert.DoesNotContain(word, vm.Status, StringComparison.OrdinalIgnoreCase);

        await vm.StopCommand.ExecuteAsync(null);
    }
}
