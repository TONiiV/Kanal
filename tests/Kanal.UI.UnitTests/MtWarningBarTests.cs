using System.Reflection;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Kanal.Core.Models;
using Kanal.Core.Providers;
using Kanal.Core.Providers.Testing;
using Kanal.Core.Room;
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

        public bool EmptyAfterFirstCall { get; init; }

        public string Id => "scripted-mt";

        public async Task<IReadOnlyDictionary<string, string>> TranslateAsync(
            string text, string from, IReadOnlyList<string> to,
            IReadOnlyList<Utterance> context, CancellationToken ct)
        {
            var call = Interlocked.Increment(ref _calls) - 1;
            await onCall(call, ct);
            if (EmptyAfterFirstCall && call > 0)
                return new Dictionary<string, string>();
            return to.ToDictionary(lang => lang, lang => $"[{lang}] {text}");
        }
    }

    private static Task Fails(string code) =>
        throw new MtProviderException("refused", code);

    private static Task Silent(CancellationToken ct) => Task.Delay(Timeout.Infinite, ct);

    private static ScriptedMt FailsOnceThenSilent(string code) =>
        new((call, ct) => call == 0 ? Fails(code) : Silent(ct));

    private static Task StopAsync(MainViewModel vm) =>
        vm.StopCommand.ExecuteAsync(null).WaitAsync(TimeSpan.FromSeconds(30));

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

    private static async Task WithMeetingAsync(
        ScriptedMt mt, Func<MainViewModel, Task> body, Action<MainViewModel>? beforeStart = null)
    {
        var vm = TestViewModels.Demo();
        beforeStart?.Invoke(vm);
        vm.PlanFilter = plan => plan with
        {
            Asr = new FakeAsrProvider(
                script: [new FakeAsrProvider.Line("S01", "zh", "好"), new FakeAsrProvider.Line("S02", "de", "Ja")],
                partialInterval: TimeSpan.FromMilliseconds(100),
                loop: true),
            Mt = mt,
        };
        await vm.StartCommand.ExecuteAsync(null).WaitAsync(TimeSpan.FromSeconds(30));
        try
        {
            await body(vm);
        }
        finally
        {
            if (vm.IsRunning)
                await StopAsync(vm);
        }
    }

    [AvaloniaFact]
    public Task AKeyErrorFromCloudTranslationRaisesTheWarning() =>
        WithMeetingAsync(FailsOnceThenSilent(MtProviderException.InvalidKey), async vm =>
        {
            await PumpUntilAsync(() => vm.HasMtWarning);

            Assert.Equal("mt.key", vm.MtWarningCode);
            Assert.Equal(Localizer.Instance["error.mt.key"], vm.MtWarningText);
            Assert.Contains("cloud translation", vm.MtWarningText);
        });

    [AvaloniaFact]
    public Task AnUnrelatedStatusWriteKeepsTheWarning() =>
        WithMeetingAsync(FailsOnceThenSilent(MtProviderException.InvalidKey), async vm =>
        {
            await PumpUntilAsync(() => vm.HasMtWarning);

            vm.Status = "Recording started.";

            Assert.True(vm.HasMtWarning);
            Assert.Equal("mt.key", vm.MtWarningCode);
        });

    [AvaloniaFact]
    public Task AFailureThatIsNotAnMtErrorRaisesNoWarning() =>
        WithMeetingAsync(
            new ScriptedMt((call, ct) => call == 0 ? throw new InvalidOperationException("boom") : Silent(ct)),
            async vm =>
            {
                await PumpUntilAsync(() => vm.Status.Contains("boom"));

                Assert.False(vm.HasMtWarning);
            });

    [AvaloniaFact]
    public Task AGoodTranslationClearsTheWarning()
    {
        var gate = new TaskCompletionSource();
        var mt = new ScriptedMt(async (call, ct) =>
        {
            if (call == 0)
                await Fails(MtProviderException.Unavailable);
            await gate.Task.WaitAsync(ct);
        });
        return WithMeetingAsync(mt, async vm =>
        {
            await PumpUntilAsync(() => vm.HasMtWarning);

            gate.SetResult();
            await PumpUntilAsync(() => !vm.HasMtWarning);

            Assert.Null(vm.MtWarningCode);
        });
    }

    [AvaloniaFact]
    public Task AnEmptyResultFromALockedProviderKeepsTheWarning()
    {
        var mt = new ScriptedMt((call, _) => call == 0 ? Fails(MtProviderException.InvalidKey) : Task.CompletedTask)
        {
            EmptyAfterFirstCall = true,
        };
        return WithMeetingAsync(mt, async vm =>
        {
            await PumpUntilAsync(() => mt.Calls >= 3);
            await Task.Delay(100);
            Dispatcher.UIThread.RunJobs();

            Assert.True(vm.HasMtWarning);
            Assert.Equal("mt.key", vm.MtWarningCode);
        });
    }

    [AvaloniaFact]
    public Task EndingTheMeetingClearsTheWarning() =>
        WithMeetingAsync(FailsOnceThenSilent(MtProviderException.QuotaExceeded), async vm =>
        {
            await PumpUntilAsync(() => vm.HasMtWarning);

            await StopAsync(vm);

            Assert.False(vm.HasMtWarning);
            Assert.Null(vm.MtWarningCode);
        });

    [AvaloniaFact]
    public Task ALateErrorFromAnEndedSessionRaisesNoWarning() =>
        WithMeetingAsync(new ScriptedMt((_, ct) => Silent(ct)), async vm =>
        {
            var session = (MeetingSession)typeof(MainViewModel)
                .GetField("_session", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(vm)!;
            var raise = (Action<AsrEvent.Error>)typeof(MeetingSession)
                .GetField(nameof(MeetingSession.ErrorOccurred), BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(session)!;
            await StopAsync(vm);

            raise(new AsrEvent.Error("late", Fatal: false, MtProviderException.InvalidKey));
            Dispatcher.UIThread.RunJobs();

            Assert.False(vm.HasMtWarning);
            Assert.Null(vm.MtWarningCode);
        });

    [AvaloniaFact]
    public Task ARepeatedCodeKeepsOneWarning()
    {
        var mt = new ScriptedMt((call, ct) => call < 4 ? Fails(MtProviderException.Unavailable) : Silent(ct));
        var changes = 0;
        return WithMeetingAsync(
            mt,
            async vm =>
            {
                await PumpUntilAsync(() => mt.Calls >= 5);
                await Task.Delay(100);
                Dispatcher.UIThread.RunJobs();

                Assert.True(vm.HasMtWarning);
                Assert.Equal("mt.unavailable", vm.MtWarningCode);
                Assert.Equal(1, changes);
            },
            vm => vm.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(MainViewModel.MtWarningCode))
                    changes++;
            });
    }

    [AvaloniaFact]
    public Task TheBarShowsTheTextAndTheButtonOpensTheTranslationSettings() =>
        WithMeetingAsync(FailsOnceThenSilent(MtProviderException.InvalidKey), async vm =>
        {
            var window = new MainWindow { DataContext = vm };
            window.Show();
            try
            {
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
            }
            finally
            {
                window.Close();
            }
        });

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
