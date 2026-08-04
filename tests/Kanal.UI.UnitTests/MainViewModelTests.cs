using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Kanal.Host.Services;
using Kanal.Host.ViewModels;

namespace Kanal.UI.UnitTests;

public class MainViewModelTests
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
    public async Task RenameSpeakerUpdatesRoomAndHistoryBubbles()
    {
        var vm = TestViewModels.Hermetic();
        vm.SelectedMode = vm.Modes.First(o => o.Mode.Id == PipelineModeId.Demo); // never touch the network

        await vm.StartCommand.ExecuteAsync(null);
        await WaitForAsync(() => vm.Speakers.Count > 0 &&
                                 vm.Columns.Any(c => c.Bubbles.Count > 0));

        var speaker = vm.Speakers[0];
        speaker.Name = "王工";
        speaker.RenameCommand.Execute(null);

        await WaitForAsync(() => vm.Columns
            .SelectMany(c => c.Bubbles)
            .Where(b => b.SpeakerTag == speaker.Tag)
            .All(b => b.SpeakerName == "王工"));

        await vm.StopCommand.ExecuteAsync(null);
    }

    [AvaloniaFact]
    public async Task RenameStillWorksAfterStop()
    {
        var vm = TestViewModels.Hermetic();
        vm.SelectedMode = vm.Modes.First(o => o.Mode.Id == PipelineModeId.Demo);

        await vm.StartCommand.ExecuteAsync(null);
        await WaitForAsync(() => vm.Speakers.Count > 0);
        await vm.StopCommand.ExecuteAsync(null);

        var speaker = vm.Speakers[0];
        speaker.Name = "Marek";
        speaker.RenameCommand.Execute(null);

        await WaitForAsync(() => vm.Speakers[0].Name == "Marek" &&
                                 vm.Columns.SelectMany(c => c.Bubbles)
                                     .Where(b => b.SpeakerTag == speaker.Tag)
                                     .All(b => b.SpeakerName == "Marek"));
    }

    [AvaloniaFact]
    public async Task StartStopStartYieldsFreshRoom()
    {
        var vm = TestViewModels.Hermetic();
        vm.SelectedMode = vm.Modes.First(o => o.Mode.Id == PipelineModeId.Demo);

        await vm.StartCommand.ExecuteAsync(null);
        await WaitForAsync(() => vm.Columns.Any(c => c.Bubbles.Count > 0));
        await vm.StopCommand.ExecuteAsync(null);

        await vm.StartCommand.ExecuteAsync(null);
        Assert.True(vm.IsRunning);
        await vm.StopCommand.ExecuteAsync(null);
    }

    /// <summary>The operator picks a microphone from this list, so it must fill on every
    /// platform that has a capture backend — not Windows only.</summary>
    [AvaloniaFact]
    public void MicrophoneListFillsOnAnySupportedPlatform()
    {
        var vm = TestViewModels.Hermetic();

        if (!Kanal.Audio.AudioCaptureFactory.IsSupported)
            return;

        Assert.NotEmpty(vm.Devices);
        Assert.Equal(vm.Devices[0], vm.SelectedDevice);
    }
}
