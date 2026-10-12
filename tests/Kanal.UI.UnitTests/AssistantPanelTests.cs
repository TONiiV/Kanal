using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Kanal.Core.Insights;
using Kanal.Core.Models;
using Kanal.Core.Providers;
using Kanal.Core.Providers.Testing;
using Kanal.Core.Room;
using Kanal.Host.ViewModels;
using Kanal.Host.Views;

namespace Kanal.UI.UnitTests;

public class AssistantPanelTests
{
    private static RoomState RoomWith(params string[] ids)
    {
        var room = new RoomState(new RoomConfig("room", ["zh", "de"]));
        foreach (var id in ids)
            room.ApplyTranscript(new AsrEvent.Transcript(
                id, "S01", $"said {id}", "zh", 0, 1000, IsFinal: true, CodeSwitch: false, 0.9, null));
        return room;
    }

    private static AssistantViewModel Following(params string[] ids)
    {
        var vm = new AssistantViewModel();
        vm.Follow(RoomWith(ids));
        return vm;
    }

    private static MeetingInsight Insight(string id, InsightKind kind, string topic, params string[] sources) =>
        new(id, kind, topic, $"text of {id}", sources);

    [Fact]
    public void WithNoAnalystConnectedThePanelSaysSoRatherThanShowingAnEmptyList()
    {
        var vm = Following("u1");

        Assert.False(vm.IsAnalystConnected);
        Assert.True(vm.ShowNoAnalystNote);
        Assert.Empty(vm.Topics);

        vm.IsAnalystConnected = true;
        Assert.False(vm.ShowNoAnalystNote);
    }

    [Fact]
    public void AnEmptyListFromAConnectedAnalystIsNotTheSameAsNoAnalyst()
    {
        var vm = Following("u1");
        vm.IsAnalystConnected = true;

        Assert.True(vm.ShowNothingFoundYet);
        Assert.False(vm.ShowNoAnalystNote);

        vm.Record(Insight("i1", InsightKind.Point, "Delivery", "u1"));
        Assert.False(vm.ShowNothingFoundYet);
    }

    private sealed class OnePointPerWindow : IMeetingAgent
    {
        public string Id => "test-agent";

        public MeetingAgentCapabilities Caps { get; } = new(new HashSet<InsightKind> { InsightKind.Point });

        public Task<IReadOnlyList<MeetingInsight>> ProposeAsync(MeetingAgentWindow window, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<MeetingInsight>>(
                [new("", InsightKind.Point, "Lieferung", "Termin vor Ende August", [window.Finals[0].Id])]);
    }

    private static MainViewModel DemoWith(IMeetingAgent? agent)
    {
        var vm = TestViewModels.Demo();
        vm.PlanFilter = plan => plan with
        {
            Asr = new FakeAsrProvider(partialInterval: TimeSpan.FromMilliseconds(5), loop: true),
            Agent = agent,
        };
        return vm;
    }

    private static async Task PumpUntilAsync(Func<bool> condition)
    {
        var deadline = Environment.TickCount64 + 15_000;
        while (!condition())
        {
            if (Environment.TickCount64 > deadline)
                throw new TimeoutException("Condition not met in time.");
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(20, TestContext.Current.CancellationToken);
        }
    }

    [AvaloniaFact]
    public async Task AConnectedAgentsCandidatesAppearInThePanelWhileTheMeetingRuns()
    {
        var vm = DemoWith(new OnePointPerWindow());

        await vm.StartCommand.ExecuteAsync(null);
        Assert.True(vm.Assistant.IsAnalystConnected);
        await PumpUntilAsync(() => vm.Assistant.Topics.Count > 0);
        await vm.StopCommand.ExecuteAsync(null);

        var topic = Assert.Single(vm.Assistant.Topics);
        Assert.Equal("Lieferung", topic.Topic);
        Assert.All(topic.Items, item => Assert.True(item.IsCandidate));
        Assert.True(vm.Assistant.IsAnalystConnected);
    }

    private sealed class GatedTranslator(SemaphoreSlim model) : IMtProvider
    {
        public int Requested;
        public int Cancelled;

        public string Id => "gated-mt";

        public async Task<IReadOnlyDictionary<string, string>> TranslateAsync(
            string text, string from, IReadOnlyList<string> to, IReadOnlyList<Utterance> context, CancellationToken ct)
        {
            Interlocked.Increment(ref Requested);
            try
            {
                await model.WaitAsync(ct);
            }
            catch (OperationCanceledException)
            {
                Interlocked.Increment(ref Cancelled);
                throw;
            }

            try
            {
                return to.ToDictionary(lang => lang, lang => $"[{lang}] {text}");
            }
            finally
            {
                model.Release();
            }
        }
    }

    private sealed class ModelHoldingAgent(SemaphoreSlim model) : IMeetingAgent
    {
        public volatile bool Holding;

        public string Id => "model-holder";

        public MeetingAgentCapabilities Caps { get; } = new(new HashSet<InsightKind> { InsightKind.Point });

        public async Task<IReadOnlyList<MeetingInsight>> ProposeAsync(MeetingAgentWindow window, CancellationToken ct)
        {
            await model.WaitAsync(ct);
            try
            {
                Holding = true;
                await Task.Delay(Timeout.Infinite, ct);
                return [];
            }
            finally
            {
                model.Release();
            }
        }
    }

    [AvaloniaFact]
    public async Task StopEndsTheAgentCallBeforeTheLastTranslationsAreDrained()
    {
        var model = new SemaphoreSlim(1, 1);
        var translator = new GatedTranslator(model);
        var agent = new ModelHoldingAgent(model);
        var vm = TestViewModels.Demo();
        vm.PlanFilter = plan => plan with
        {
            Asr = new FakeAsrProvider(partialInterval: TimeSpan.FromMilliseconds(5), loop: true),
            Mt = translator,
            Agent = agent,
        };

        await vm.StartCommand.ExecuteAsync(null);
        await PumpUntilAsync(() => agent.Holding);
        var before = Volatile.Read(ref translator.Requested);
        await PumpUntilAsync(() => Volatile.Read(ref translator.Requested) > before);
        await vm.StopCommand.ExecuteAsync(null);

        Assert.Equal(0, Volatile.Read(ref translator.Cancelled));
    }

    [AvaloniaFact]
    public async Task WithoutAnAgentTheMeetingRunsAndThePanelSaysNoneIsConnected()
    {
        var vm = DemoWith(null);

        await vm.StartCommand.ExecuteAsync(null);
        Assert.True(vm.IsRunning);
        Assert.False(vm.Assistant.IsAnalystConnected);
        await vm.StopCommand.ExecuteAsync(null);
    }

    [Fact]
    public void ANewMeetingStartsWithNoAnalystUntilOneIsConnected()
    {
        var vm = Following("u1");
        vm.IsAnalystConnected = true;

        vm.Forget();

        Assert.False(vm.IsAnalystConnected);
    }

    [Fact]
    public void EachItemCarriesTheWordsThatSupportIt()
    {
        var vm = Following("u1", "u2");
        vm.Record(Insight("i1", InsightKind.Decision, "Delivery", "u1", "u2"));

        var item = vm.Topics.Single().Items.Single();
        Assert.Equal(["said u1", "said u2"], item.Sources);
    }

    [Fact]
    public void ACandidateNeverReadsAsAnAcceptedCommitment()
    {
        var vm = Following("u1");
        vm.Record(Insight("i1", InsightKind.Decision, "Delivery", "u1"));

        var item = vm.Topics.Single().Items.Single();
        Assert.True(item.IsCandidate);
        Assert.NotEqual(item.StateLabel, Kanal.Host.Localization.Localizer.Instance["insight.confirmed"]);

        item.ConfirmCommand.Execute(null);

        var confirmed = vm.Topics.Single().Items.Single();
        Assert.False(confirmed.IsCandidate);
        Assert.Equal(Kanal.Host.Localization.Localizer.Instance["insight.confirmed"], confirmed.StateLabel);
    }

    [Fact]
    public void DismissingAnItemTakesItOffThePanel()
    {
        var vm = Following("u1");
        vm.Record(Insight("i1", InsightKind.Proposal, "Price", "u1"));

        vm.Topics.Single().Items.Single().DismissCommand.Execute(null);

        Assert.Empty(vm.Topics);
    }

    [Fact]
    public void AnItemThatLeadsBackToNothingSaidNeverReachesThePanel()
    {
        var vm = Following("u1");

        vm.Record(Insight("i1", InsightKind.Decision, "Delivery", "u404"));

        Assert.Empty(vm.Topics);
    }

    private static SidePanelView Panel(out Window window, out MainViewModel vm)
    {
        vm = TestViewModels.Hermetic();
        window = new MainWindow { DataContext = vm, Width = 1320, Height = 820 };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return window.GetLogicalDescendants().OfType<SidePanelView>().Single();
    }

    // The selected pane is a logical child of both its TabItem and the TabControl's presenter,
    // so an unfiltered walk meets the same control twice.
    private static Control Named(Control root, string name) =>
        root.GetLogicalDescendants().OfType<Control>()
            .Where(control => control.Name == name).Distinct().Single();

    [AvaloniaFact]
    public void PointsIsTheOnlyTabOnScreenUntilDiarizationLands()
    {
        var panel = Panel(out var window, out _);
        var tabs = panel.GetLogicalDescendants().OfType<TabControl>().Single();

        Assert.Equal(
            ["PointsTab", "SpeakersTab", "FilesTab"], tabs.Items.OfType<TabItem>().Select(t => t.Name));
        foreach (var tab in tabs.Items.OfType<TabItem>())
            Assert.False(string.IsNullOrWhiteSpace(
                Avalonia.Automation.AutomationProperties.GetName(tab)));

        var points = tabs.Items.OfType<TabItem>().Single(tab => tab.Name == "PointsTab");
        var speakers = tabs.Items.OfType<TabItem>().Single(tab => tab.Name == "SpeakersTab");

        Assert.True(points.IsVisible);
        Assert.True(points.IsSelected);
        Assert.False(speakers.IsVisible);

        window.Close();
    }

    [AvaloniaFact]
    public void TheSpeakerControlsSurvivedTheMoveIntoATab()
    {
        var panel = Panel(out var window, out _);

        foreach (var name in new[] { "SpeakerList", "MergeFrom", "MergeInto", "MergeSpeakers" })
            Named(panel, name);

        window.Close();
    }

    [AvaloniaFact]
    public void TheFilesTabOpensTheShownMeetingsFolderAndIsIdleWithoutOne()
    {
        var panel = Panel(out var window, out var vm);

        Named(panel, "ImportFile");
        var open = Assert.IsType<Button>(Named(panel, "OpenFolder"));
        Assert.Same(vm.Files.OpenFolderCommand, open.Command);
        Assert.False(open.IsEffectivelyEnabled);

        window.Close();
    }

    [AvaloniaFact]
    public void WithNoModelConnectedThePanelSaysSoWhereTheItemsWouldGo()
    {
        var panel = Panel(out var window, out var vm);

        Assert.True(Named(panel, "NoAnalystNote").IsVisible);
        Assert.False(Named(panel, "InsightList").IsVisible);

        vm.Assistant.IsAnalystConnected = true;
        Dispatcher.UIThread.RunJobs();

        Assert.False(Named(panel, "NoAnalystNote").IsVisible);
        Assert.True(Named(panel, "NothingYetNote").IsVisible);

        window.Close();
    }
}
