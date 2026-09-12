using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Kanal.Core.Insights;
using Kanal.Core.Models;
using Kanal.Core.Providers;
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
    public void TheAssistantPanelIsPointsThenSpeakersThenFiles()
    {
        var panel = Panel(out var window, out _);
        var tabs = panel.GetLogicalDescendants().OfType<TabControl>().Single();

        Assert.Equal(
            ["PointsTab", "SpeakersTab", "FilesTab"], tabs.Items.OfType<TabItem>().Select(t => t.Name));
        foreach (var tab in tabs.Items.OfType<TabItem>())
            Assert.False(string.IsNullOrWhiteSpace(
                Avalonia.Automation.AutomationProperties.GetName(tab)));

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
