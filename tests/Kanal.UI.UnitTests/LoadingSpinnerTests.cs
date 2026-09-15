using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Kanal.Core.Models;
using Kanal.Core.Providers;
using Kanal.Host.Views;

namespace Kanal.UI.UnitTests;

public class LoadingSpinnerTests
{
    private sealed class GatedWarmupMt(Task gate) : IMtProvider, IWarmupProvider
    {
        public string Id => "gated-warmup";

        public async Task WarmUpAsync(CancellationToken ct) => await gate.WaitAsync(ct);

        public Task<IReadOnlyDictionary<string, string>> TranslateAsync(
            string text, string from, IReadOnlyList<string> to,
            IReadOnlyList<Utterance> context, CancellationToken ct) =>
            Task.FromResult<IReadOnlyDictionary<string, string>>(
                to.ToDictionary(l => l, l => text));
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

    private static Control Spinner(Window window) =>
        window.GetLogicalDescendants().OfType<Control>().Single(c => c.Name == "StartingSpinner");

    [AvaloniaFact]
    public async Task TheSpinnerShowsWhileTheModelLoadsAndHidesOnceItIsReady()
    {
        var vm = TestViewModels.Demo();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var mt = new GatedWarmupMt(gate.Task);
        vm.PlanFilter = plan => plan with { Mt = mt };

        var window = new MainWindow { DataContext = vm };
        window.Show();
        var spinner = Spinner(window);
        Assert.False(spinner.IsVisible, "the spinner ran before Start was ever pressed.");

        var starting = vm.StartCommand.ExecuteAsync(null);
        await PumpAsync(100);

        Assert.True(spinner.IsVisible, "nothing shows the model load is in progress.");
        Assert.Equal("StopMark", spinner.FindLogicalAncestorOfType<Button>()?.Name);

        gate.SetResult();
        await starting;
        await PumpAsync(100);

        Assert.False(spinner.IsVisible, "the spinner kept running after the model finished loading.");
        await vm.StopCommand.ExecuteAsync(null);
        window.Close();
    }

    [AvaloniaFact]
    public async Task TheStatusNamesTheModelThatIsLoading()
    {
        var vm = TestViewModels.Demo();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var mt = new GatedWarmupMt(gate.Task);
        vm.PlanFilter = plan =>
            plan with { Mt = mt, Status = plan.Status with { TranslationLabel = "Translation: Nomlex-7B (local)" } };

        var starting = vm.StartCommand.ExecuteAsync(null);
        await PumpAsync(100);

        Assert.Contains("Nomlex-7B", vm.Status);

        gate.SetResult();
        await starting;
        await vm.StopCommand.ExecuteAsync(null);
    }
}
