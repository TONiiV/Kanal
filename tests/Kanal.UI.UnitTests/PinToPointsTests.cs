using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Kanal.Core.Models;
using Kanal.Core.Providers;
using Kanal.Core.Room;
using Kanal.Host.Localization;
using Kanal.Host.ViewModels;
using Kanal.Host.Views;

namespace Kanal.UI.UnitTests;

public class PinToPointsTests
{
    private static RoomState RoomWith(params string[] ids)
    {
        var room = new RoomState(new RoomConfig("room", ["zh", "de"]));
        var start = 0L;
        foreach (var id in ids)
        {
            room.ApplyTranscript(new AsrEvent.Transcript(
                id, "S01", $"said {id}", "zh", start, start + 1000, IsFinal: true, CodeSwitch: false, 0.9, null));
            start += 65_000;
        }

        return room;
    }

    private static AssistantViewModel Following(params string[] ids)
    {
        var vm = new AssistantViewModel { SpeakerName = _ => "Wang" };
        vm.Follow(RoomWith(ids));
        return vm;
    }

    [Fact]
    public void APinShowsWithoutAnAnalystWithItsTextSpeakerAndTime()
    {
        var vm = Following("u1", "u2");

        vm.Pin("u2", "Das Bauteil passt nicht.");

        Assert.False(vm.IsAnalystConnected);
        var item = vm.Topics.Single().Items.Single();
        Assert.Equal("Das Bauteil passt nicht.", item.Text);
        Assert.Equal("Wang · 00:01:05", item.Attribution);
        Assert.True(item.IsPinned);
        Assert.False(item.IsCandidate);
        Assert.Equal(Localizer.Instance["insight.pinned"], item.KindLabel);
        Assert.True(vm.ShowInsightList);
        Assert.False(vm.ShowNoAnalystNote);
    }

    [Fact]
    public void ASecondPinOfTheSameUtteranceDoesNothing()
    {
        var vm = Following("u1");

        vm.Pin("u1", "first");
        vm.Pin("u1", "second");

        Assert.Equal(["first"], vm.Topics.Single().Items.Select(i => i.Text));
    }

    [Fact]
    public void UnpinningTakesThePointOffAndAllowsAPinAgain()
    {
        var vm = Following("u1");
        vm.Pin("u1", "first");

        vm.Topics.Single().Items.Single().DismissCommand.Execute(null);

        Assert.Empty(vm.Topics);
        Assert.True(vm.ShowNoAnalystNote);
        Assert.False(vm.ShowInsightList);
        vm.Pin("u1", "again");
        Assert.Equal(["again"], vm.Topics.Single().Items.Select(i => i.Text));
    }

    [Fact]
    public void ThePinnedHeadingAndTheUnpinTipAreLocalised()
    {
        var vm = Following("u1");
        vm.Pin("u1", "first");

        Assert.Equal(Localizer.Instance["insight.pinned.heading"], vm.Topics.Single().Topic);
        Assert.Equal(Localizer.Instance["insight.unpin.tip"], vm.Topics.Single().Items.Single().DismissTip);
    }

    [Fact]
    public void PinningBeforeAnyRoomIsFollowedDoesNothing()
    {
        var vm = new AssistantViewModel();

        vm.Pin("u1", "text");

        Assert.Empty(vm.Topics);
    }

    [AvaloniaFact]
    public void ThePinCommandOfALineReachesTheHandlerAndNeedsOne()
    {
        var pinned = new List<string>();
        var bubble = new ColumnViewModel("de", null, (b, text) => pinned.Add($"{b.UtteranceId}:{text}")).GetOrAdd("u1");
        var unwired = new ColumnViewModel("de").GetOrAdd("u1");
        bubble.IsPartial = false;
        unwired.IsPartial = false;

        Assert.True(bubble.PinCommand.CanExecute("Hallo"));
        bubble.PinCommand.Execute("Hallo");

        Assert.Equal(["u1:Hallo"], pinned);
        Assert.False(unwired.PinCommand.CanExecute("Hallo"));
    }

    [AvaloniaFact]
    public void ALineCannotBePinnedBeforeItIsFinalAndTranslated()
    {
        var bubble = new ColumnViewModel("de", null, (_, _) => { }).GetOrAdd("u1");
        var changes = 0;
        bubble.PinCommand.CanExecuteChanged += (_, _) => changes++;

        Assert.False(bubble.PinCommand.CanExecute("x"));

        bubble.IsPartial = false;
        bubble.AwaitingTranslation = true;
        Assert.False(bubble.PinCommand.CanExecute("x"));

        bubble.AwaitingTranslation = false;
        Assert.True(bubble.PinCommand.CanExecute("x"));
        Assert.True(changes >= 2);
    }

    [AvaloniaFact]
    public void ARenameAndAMergeReachThePinnedSpeaker()
    {
        var vm = TestViewModels.Hermetic();
        vm.Assistant.Follow(RoomWith("u1"));
        vm.Assistant.Pin("u1", "text");
        string Attribution() => vm.Assistant.Topics.Single().Items.Single().Attribution!;
        Assert.Equal("S01 · 00:00:00", Attribution());

        vm.ApplySpeaker(new Speaker("S01", "Marek", [], RoomState.Palette[0]));
        Assert.Equal("Marek · 00:00:00", Attribution());

        vm.ApplySpeaker(new Speaker("S02", "Anna", ["S01"], RoomState.Palette[1]));
        Assert.Equal("Anna · 00:00:00", Attribution());
    }

    [Fact]
    public void ANewMeetingDropsThePinsOfTheOldOne()
    {
        var vm = Following("u1");
        vm.Pin("u1", "text");

        vm.Follow(RoomWith("u1"));

        Assert.Empty(vm.Topics);
    }

    [AvaloniaFact]
    public void ALanguageSwitchRelabelsThePinnedPoints()
    {
        var previous = Localizer.Instance.Current;
        try
        {
            var vm = TestViewModels.Hermetic();
            vm.Assistant.Follow(RoomWith("u1"));
            vm.Assistant.Pin("u1", "text");

            Localizer.Instance.Current = "de";

            var topic = vm.Assistant.Topics.Single();
            Assert.Equal(Localizer.Instance["insight.pinned.heading"], topic.Topic);
            Assert.Equal(Localizer.Instance["insight.pinned"], topic.Items.Single().KindLabel);
            Assert.NotEqual("Pinned", topic.Topic);
        }
        finally
        {
            Localizer.Instance.Current = previous;
        }
    }

    [AvaloniaFact]
    public void TheSharedMenuHasAPinItem()
    {
        var vm = TestViewModels.Hermetic();
        vm.Columns.Add(new ColumnViewModel("de", null, (_, _) => { }));
        vm.Columns[0].GetOrAdd("u1").Text = "Hallo";
        var window = new MainWindow { DataContext = vm, Width = 1320, Height = 820 };
        window.Show();
        for (var i = 0; i < 4; i++)
        {
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
        }

        var block = window.GetVisualDescendants().OfType<SelectableTextBlock>().First();
        var flyout = Assert.IsType<MenuFlyout>(block.ContextFlyout);
        var items = flyout.Items.OfType<MenuItem>().ToList();
        Assert.Equal(["CopyLine", "CopyWithSpeaker", "PinToPoints"], items.Select(i => i.Name));
        Assert.Equal(Localizer.Instance["transcript.pin"], items[2].Header);
        window.Close();
    }

    [AvaloniaFact]
    public void AMeetingWithOnlyPinsShowsThePointsListNotTheNoAnalystNote()
    {
        var vm = TestViewModels.Hermetic();
        vm.Assistant.Follow(RoomWith("u1"));
        vm.Assistant.Pin("u1", "Hallo");
        var window = new MainWindow { DataContext = vm, Width = 1320, Height = 820 };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        var panel = window.GetLogicalDescendants().OfType<SidePanelView>().Single();

        Control Named(string name) => panel.GetLogicalDescendants().OfType<Control>()
            .Where(control => control.Name == name).Distinct().Single();

        Assert.True(Named("InsightList").IsVisible);
        Assert.False(Named("NoAnalystNote").IsVisible);
        window.Close();
    }
}
