using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Kanal.Core.Models;
using Kanal.Core.Providers;
using Kanal.Host.ViewModels;

namespace Kanal.UI.UnitTests;

/// <summary>
/// Start with a local translation model used to open the room first and load the weights on the
/// first final — so the meeting began with the opening sentences untranslated for however long
/// llama.cpp took to map a multi-gigabyte file, and nothing on screen said why. Now the model
/// loads to a working state before transcription starts: the masthead says so while it happens,
/// a load that fails stops the Start instead of opening a room that cannot translate, and the
/// operator can abort the load with Stop.
/// </summary>
public class WarmupViewModelTests
{
    /// <summary>A warmable translator whose load is held open by the test.</summary>
    private sealed class GatedWarmupMt(Task gate) : IMtProvider, IWarmupProvider
    {
        public int WarmUpCalls;

        public string Id => "gated-warmup";

        public async Task WarmUpAsync(CancellationToken ct)
        {
            Interlocked.Increment(ref WarmUpCalls);
            await gate.WaitAsync(ct);
        }

        public Task<IReadOnlyDictionary<string, string>> TranslateAsync(
            string text, string from, IReadOnlyList<string> to,
            IReadOnlyList<Utterance> context, CancellationToken ct) =>
            Task.FromResult<IReadOnlyDictionary<string, string>>(
                to.ToDictionary(l => l, l => $"[{from}→{l}] {text}"));
    }

    /// <summary>Wraps the planned ASR provider so the test can see whether transcription began.</summary>
    private sealed class RecordingAsr(IAsrProvider inner) : IAsrProvider
    {
        public int Starts;

        public string Id => inner.Id;
        public AsrCapabilities Caps => inner.Caps;

        public Task<IAsrSession> StartAsync(AsrSessionOptions options, CancellationToken ct)
        {
            Interlocked.Increment(ref Starts);
            return inner.StartAsync(options, ct);
        }
    }

    private static async Task PumpAsync(int ms)
    {
        var deadline = Environment.TickCount64 + ms;
        while (Environment.TickCount64 < deadline)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(20);
        }

        Dispatcher.UIThread.RunJobs();
    }

    private static (MainViewModel Vm, GatedWarmupMt Mt, Func<RecordingAsr?> Asr) DemoWithGatedModel(Task gate)
    {
        var vm = TestViewModels.Demo();
        var mt = new GatedWarmupMt(gate);
        RecordingAsr? asr = null;
        vm.PlanFilter = plan =>
        {
            asr = new RecordingAsr(plan.Asr!);
            return plan with { Asr = asr, Mt = mt };
        };
        return (vm, mt, () => asr);
    }

    [AvaloniaFact]
    public async Task StartLoadsTheModelBeforeTranscriptionBegins()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var (vm, mt, asr) = DemoWithGatedModel(gate.Task);

        var starting = vm.StartCommand.ExecuteAsync(null);
        await PumpAsync(100);

        Assert.Equal(1, mt.WarmUpCalls);
        Assert.Contains("Loading the translation model", vm.Status);
        Assert.False(vm.IsRunning, "the room went live while the model was still loading.");
        Assert.Equal(0, asr()!.Starts);
        Assert.False(vm.StartCommand.CanExecute(null), "a second Start was offered mid-load.");
        Assert.True(vm.StopCommand.CanExecute(null), "the load cannot be aborted.");

        gate.SetResult();
        await starting;
        await PumpAsync(100);

        Assert.True(vm.IsRunning);
        Assert.Equal(1, asr()!.Starts);
        await vm.StopCommand.ExecuteAsync(null);
    }

    [AvaloniaFact]
    public async Task AFailedLoadIsReportedAndNothingStarts()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        gate.SetException(new InvalidOperationException("weights corrupt"));
        var (vm, _, asr) = DemoWithGatedModel(gate.Task);

        await vm.StartCommand.ExecuteAsync(null);
        await PumpAsync(50);

        Assert.Contains("Translation model failed to load", vm.Status);
        Assert.Contains("weights corrupt", vm.Status);
        Assert.False(vm.IsRunning);
        Assert.Equal(0, asr()!.Starts);
        Assert.True(vm.StartCommand.CanExecute(null), "Start never came back after a failed load.");
        Assert.False(vm.StopCommand.CanExecute(null));
    }

    [AvaloniaFact]
    public async Task StopDuringTheLoadAbortsItCleanly()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var (vm, mt, asr) = DemoWithGatedModel(gate.Task);

        var starting = vm.StartCommand.ExecuteAsync(null);
        await PumpAsync(100);
        Assert.Equal(1, mt.WarmUpCalls);

        await vm.StopCommand.ExecuteAsync(null);
        await starting;
        await PumpAsync(50);

        Assert.False(vm.IsRunning);
        Assert.Equal(0, asr()!.Starts);
        Assert.DoesNotContain("failed", vm.Status);
        Assert.True(vm.StartCommand.CanExecute(null), "Start never came back after an aborted load.");
        Assert.False(vm.StopCommand.CanExecute(null));
    }

    /// <summary>The scripted demo translator has nothing to preload; Start must not gain a
    /// loading phase where there is nothing to load.</summary>
    [AvaloniaFact]
    public async Task ATranslatorWithNothingToLoadStartsStraightAway()
    {
        var vm = TestViewModels.Demo();

        await vm.StartCommand.ExecuteAsync(null);

        Assert.True(vm.IsRunning);
        await vm.StopCommand.ExecuteAsync(null);
    }
}
