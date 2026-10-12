using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Kanal.Core.Models;
using Kanal.Core.Room;
using Kanal.Host.Services;
using Kanal.Host.ViewModels;

namespace Kanal.UI.UnitTests;

public class SpeakerManagementTests
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

    private static Speaker Person(string tag, string? name = null, params string[] mergedFrom) =>
        new(tag, name, mergedFrom, RoomState.Palette[0]);

    private static void Say(ColumnViewModel column, string id, string tag) =>
        column.GetOrAdd(id).SpeakerTag = tag;

    [AvaloniaFact]
    public void TheSpeakersTabAppearsWithTheFirstSpeaker()
    {
        var vm = TestViewModels.Hermetic();
        var changes = new List<string?>();
        vm.PropertyChanged += (_, e) => changes.Add(e.PropertyName);

        Assert.False(vm.HasSpeakers);

        vm.ApplySpeaker(Person("S01"));

        Assert.True(vm.HasSpeakers);
        Assert.Contains(nameof(MainViewModel.HasSpeakers), changes);
    }

    [AvaloniaFact]
    public void EachRowOffersTheOtherSpeakersToMergeInto()
    {
        var vm = TestViewModels.Hermetic();

        vm.ApplySpeaker(Person("S01"));
        Assert.False(vm.Speakers[0].CanMerge);

        vm.ApplySpeaker(Person("S02", "Marek"));
        vm.ApplySpeaker(Person("S03"));

        Assert.Equal(["S02 · Marek", "S03"], vm.Speakers[0].MergeTargets.Select(t => t.Label));
        Assert.Equal(["S01", "S03"], vm.Speakers[1].MergeTargets.Select(t => t.Tag));
        Assert.True(vm.Speakers[0].CanMerge);
    }

    [AvaloniaFact]
    public void ARowCountsItsUtterancesAndAbsorbsThoseOfAMergedSpeaker()
    {
        var vm = TestViewModels.Hermetic();
        var column = new ColumnViewModel("de");
        vm.Columns.Add(column);
        Say(column, "u1", "S01");
        Say(column, "u2", "S01");
        Say(column, "u3", "S02");

        vm.ApplySpeaker(Person("S01"));
        vm.ApplySpeaker(Person("S02"));

        Assert.Equal([2, 1], vm.Speakers.Select(s => s.UtteranceCount));

        vm.ApplySpeaker(Person("S01", null, "S02"));

        var row = Assert.Single(vm.Speakers);
        Assert.Equal(3, row.UtteranceCount);
        Assert.Empty(row.MergeTargets);
    }

    [AvaloniaFact]
    public void TheSelectedColourIsTheOneTheSpeakerHas()
    {
        var vm = TestViewModels.Hermetic();

        vm.ApplySpeaker(new Speaker("S01", null, [], RoomState.Palette[2]));

        Assert.Equal(RoomState.Palette.Count, vm.Speakers[0].Colours.Count);
        Assert.Equal(RoomState.Palette[2], Assert.Single(vm.Speakers[0].Colours, c => c.IsSelected).Value);

        vm.ApplySpeaker(new Speaker("S01", null, [], RoomState.Palette[5]));

        Assert.Equal(RoomState.Palette[5], Assert.Single(vm.Speakers[0].Colours, c => c.IsSelected).Value);
    }

    [AvaloniaFact]
    public async Task PickingAColourRecoloursTheSpeakerAndItsHistory()
    {
        var vm = TestViewModels.Hermetic();
        vm.SelectedMode = vm.Modes.First(o => o.Mode.Id == PipelineModeId.Demo);
        await vm.StartCommand.ExecuteAsync(null);
        await WaitForAsync(() => vm.Speakers.Count > 0 && vm.Columns.Any(c => c.Bubbles.Count > 0));

        var row = vm.Speakers[0];
        var pick = row.Colours.First(c => c.Value != row.Color);
        pick.PickCommand.Execute(null);

        await WaitForAsync(() => row.Color == pick.Value && pick.IsSelected &&
                                 vm.Columns.SelectMany(c => c.Bubbles)
                                     .Where(b => b.SpeakerTag == row.Tag)
                                     .All(b => b.SpeakerColor == pick.Value));

        await vm.StopCommand.ExecuteAsync(null);
    }

    [AvaloniaFact]
    public async Task MergingIntoAnotherSpeakerFoldsTheRowIntoItsTarget()
    {
        var vm = TestViewModels.Hermetic();
        vm.SelectedMode = vm.Modes.First(o => o.Mode.Id == PipelineModeId.Demo);
        await vm.StartCommand.ExecuteAsync(null);
        await WaitForAsync(() => vm.Speakers.Count >= 2);

        var from = vm.Speakers[1];
        var into = vm.Speakers[0];
        from.SelectedMergeTarget = from.MergeTargets.Single(t => t.Tag == into.Tag);

        await WaitForAsync(() => vm.Speakers.All(s => s.Tag != from.Tag) &&
                                 into.MergedFromLabel.Contains(from.Tag));
        Assert.DoesNotContain(vm.Speakers, s => s.MergeTargets.Any(t => t.Tag == from.Tag));

        await vm.StopCommand.ExecuteAsync(null);
    }
}
