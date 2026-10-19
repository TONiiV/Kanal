using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Kanal.Core.Models;
using Kanal.Core.Providers;
using Kanal.Core.Providers.Testing;
using Kanal.Host.Localization;
using Kanal.Host.ViewModels;
using Kanal.Host.Views;

namespace Kanal.UI.UnitTests;

public class MtWarningBarTests
{
    private sealed class ScriptedMt(Func<int, CancellationToken, Task> onCall) : IMtProvider
    {
        private int _calls;

        public int Calls => Volatile.Read(ref _calls);

        public string Id => "scripted-mt";

        public async Task<IReadOnlyDictionary<string, string>> TranslateAsync(
            string text, string from, IReadOnlyList<string> to,
            IReadOnlyList<Utterance> context, CancellationToken ct)
        {
            await onCall(Interlocked.Increment(ref _calls) - 1, ct);
            return to.ToDictionary(lang => lang, lang => $"[{lang}] {text}");
        }
    }

    private static Task Fails(string code) =>
        throw new MtProviderException("refused", code);

    private static async Task PumpUntilAsync(Func<bool> condition, int timeoutMs = 15_000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (!condition())
        {
            if (Environment.TickCount64 > deadline)
                throw new TimeoutException("Condition not met in time.");
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(20);
        }

        Dispatcher.UIThread.RunJobs();
    }

    private static async Task<MainViewModel> StartedAsync(ScriptedMt mt)
    {
        var vm = TestViewModels.Demo();
        vm.PlanFilter = plan => plan with
        {
            Asr = new FakeAsrProvider(
                script: [new FakeAsrProvider.Line("S01", "zh", "好"), new FakeAsrProvider.Line("S02", "de", "Ja")],
                partialInterval: TimeSpan.FromMilliseconds(10),
                loop: true),
            Mt = mt,
        };
        await vm.StartCommand.ExecuteAsync(null);
        return vm;
    }

    [AvaloniaFact]
    public async Task AKeyErrorFromCloudTranslationRaisesTheWarning()
    {
        var vm = await StartedAsync(new ScriptedMt((_, _) => Fails(MtProviderException.InvalidKey)));

        await PumpUntilAsync(() => vm.HasMtWarning);

        Assert.Equal("mt.key", vm.MtWarningCode);
        Assert.Equal(Localizer.Instance["error.mt.key"], vm.MtWarningText);
        Assert.Contains("cloud translation", vm.MtWarningText);
        await vm.StopCommand.ExecuteAsync(null);
    }

    [AvaloniaFact]
    public async Task AnUnrelatedStatusWriteKeepsTheWarning()
    {
        var vm = await StartedAsync(new ScriptedMt((_, _) => Fails(MtProviderException.InvalidKey)));
        await PumpUntilAsync(() => vm.HasMtWarning);

        vm.Status = "Recording started.";

        Assert.True(vm.HasMtWarning);
        Assert.Equal("mt.key", vm.MtWarningCode);
        await vm.StopCommand.ExecuteAsync(null);
    }

    [AvaloniaFact]
    public async Task AFailureThatIsNotAnMtErrorRaisesNoWarning()
    {
        var vm = await StartedAsync(new ScriptedMt((_, _) => throw new InvalidOperationException("boom")));
        await PumpUntilAsync(() => vm.Status.Contains("boom"));

        Assert.False(vm.HasMtWarning);
        await vm.StopCommand.ExecuteAsync(null);
    }

    [AvaloniaFact]
    public async Task AGoodTranslationClearsTheWarning()
    {
        var gate = new TaskCompletionSource();
        var mt = new ScriptedMt(async (call, ct) =>
        {
            if (call == 0)
                await Fails(MtProviderException.Unavailable);
            await gate.Task.WaitAsync(ct);
        });
        var vm = await StartedAsync(mt);
        await PumpUntilAsync(() => vm.HasMtWarning);

        gate.SetResult();
        await PumpUntilAsync(() => !vm.HasMtWarning);

        Assert.Null(vm.MtWarningCode);
        await vm.StopCommand.ExecuteAsync(null);
    }

    [AvaloniaFact]
    public async Task EndingTheMeetingClearsTheWarning()
    {
        var vm = await StartedAsync(new ScriptedMt((_, _) => Fails(MtProviderException.QuotaExceeded)));
        await PumpUntilAsync(() => vm.HasMtWarning);

        await vm.StopCommand.ExecuteAsync(null);

        Assert.False(vm.HasMtWarning);
        Assert.Null(vm.MtWarningCode);
    }

    [AvaloniaFact]
    public async Task ARepeatedCodeKeepsOneWarning()
    {
        var mt = new ScriptedMt((_, _) => Fails(MtProviderException.Unavailable));
        var vm = await StartedAsync(mt);
        await PumpUntilAsync(() => vm.HasMtWarning);

        var changes = 0;
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(MainViewModel.MtWarningCode) or nameof(MainViewModel.HasMtWarning))
                changes++;
        };
        var seen = mt.Calls;
        await PumpUntilAsync(() => mt.Calls >= seen + 3);
        await PumpUntilAsync(() => true);

        Assert.True(vm.HasMtWarning);
        Assert.Equal("mt.unavailable", vm.MtWarningCode);
        Assert.Equal(0, changes);
        await vm.StopCommand.ExecuteAsync(null);
    }

    [AvaloniaFact]
    public async Task TheBarShowsTheTextAndTheButtonOpensTheTranslationSettings()
    {
        var vm = await StartedAsync(new ScriptedMt((_, _) => Fails(MtProviderException.InvalidKey)));
        var window = new MainWindow { DataContext = vm };
        window.Show();
        var opened = 0;
        vm.ShowTranslationSettings = () =>
        {
            opened++;
            return Task.CompletedTask;
        };

        await PumpUntilAsync(() => vm.HasMtWarning);

        var bar = window.GetLogicalDescendants().OfType<Border>().Single(b => b.Name == "MtWarningBar");
        Assert.True(bar.IsVisible);
        var text = bar.GetLogicalDescendants().OfType<TextBlock>().Single(t => t.Name == "MtWarningText");
        Assert.Equal(vm.MtWarningText, text.Text);
        var button = bar.GetLogicalDescendants().OfType<Button>().Single(b => b.Name == "MtWarningSettings");
        Assert.Same(vm.OpenTranslationSettingsCommand, button.Command);
        button.Command!.Execute(null);
        Assert.Equal(1, opened);

        window.Close();
        await vm.StopCommand.ExecuteAsync(null);
    }

    [AvaloniaFact]
    public void TheBarIsHiddenWhileNothingFailed()
    {
        var window = new MainWindow { DataContext = TestViewModels.Demo() };
        window.Show();

        var bar = window.GetLogicalDescendants().OfType<Border>().Single(b => b.Name == "MtWarningBar");
        Assert.False(bar.IsVisible);

        window.Close();
    }

    [AvaloniaFact]
    public void SettingsOpenOnTheTranslationTabWhenAsked()
    {
        var window = new SettingsWindow(new SettingsViewModel { SelectedTab = SettingsViewModel.TranslationTab });
        window.Show();

        var tabs = Assert.Single(window.GetLogicalDescendants().OfType<TabControl>());
        var selected = Assert.IsType<TabItem>(tabs.SelectedItem);
        Assert.Equal(Localizer.Instance["settings.tab.translation"], AutomationProperties.GetName(selected));
        Assert.Same(selected, tabs.Items.OfType<TabItem>().ElementAt(SettingsViewModel.TranslationTab));

        window.Close();
    }
}
