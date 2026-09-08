using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Kanal.Core.Models;
using Kanal.Host.ViewModels;

namespace Kanal.UI.UnitTests;

/// <summary>
/// The navigation ruler beside the transcript. Everything asserted here is the model behind the
/// marks — which ticks exist for a given run of utterances, what each one previews, where a click
/// lands, and what happens when a long meeting has more speaker turns than the strip has slots.
/// Nothing here asserts a pixel; the strip's geometry is a XAML concern.
/// </summary>
public class TranscriptRulerTests
{
    private readonly Dictionary<string, string> _canonical = new();
    private readonly Dictionary<string, string> _names = new();

    private TranscriptRulerViewModel Ruler() => new(tag =>
    {
        var canonical = _canonical.TryGetValue(tag, out var c) ? c : tag;
        var name = _names.TryGetValue(canonical, out var n) ? n : canonical.ToUpperInvariant();
        return (canonical, name, canonical == "s1" ? "#B23A2E" : "#1C6B58");
    });

    private static Utterance Said(
        string id,
        string tag,
        string text,
        long startMs = 0,
        UtteranceState state = UtteranceState.Final) =>
        new(id, tag, startMs, startMs + 900, "zh", text, 1, state, false, 1.0,
            new Dictionary<string, string>());

    [AvaloniaFact]
    public void NothingSaidYetMeansNothingToNavigate()
    {
        Assert.Empty(Ruler().Ticks);
    }

    [AvaloniaFact]
    public void ASpeakerChangeOpensATickAndTheSameSpeakerCarryingOnDoesNot()
    {
        var ruler = Ruler();

        ruler.Observe(Said("u1", "s1", "第一句"));
        ruler.Observe(Said("u2", "s1", "第二句"));
        ruler.Observe(Said("u3", "s2", "Trzecie zdanie"));
        ruler.Observe(Said("u4", "s2", "Czwarte"));
        ruler.Observe(Said("u5", "s1", "第五句"));

        Assert.Equal(3, ruler.Ticks.Count);
        Assert.Equal(["u1", "u3", "u5"], ruler.Ticks.Select(t => t.AnchorUtteranceId));
        Assert.Equal([2, 2, 1], ruler.Ticks.Select(t => t.Utterances));
    }

    [AvaloniaFact]
    public void ATickPreviewsTheOpeningLineOfItsTurnAndFollowsThatLinesRevisions()
    {
        var ruler = Ruler();

        ruler.Observe(Said("u1", "s1", "我们把", state: UtteranceState.Partial));
        Assert.Equal("我们把", ruler.Ticks[0].Preview);

        ruler.Observe(Said("u1", "s1", "我们把 KX-4402 的公差改了"));
        ruler.Observe(Said("u2", "s1", "下周交货"));

        // still the opening line: the tick marks where the turn began, not where it got to
        Assert.Equal("我们把 KX-4402 的公差改了", ruler.Ticks[0].Preview);
        Assert.Equal("S1", ruler.Ticks[0].SpeakerName);
    }

    [AvaloniaFact]
    public void ClickingATickAsksToJumpToTheFirstUtteranceOfThatTurn()
    {
        var ruler = Ruler();
        string? landed = null;
        ruler.JumpRequested += id => landed = id;

        ruler.Observe(Said("u1", "s1", "一"));
        ruler.Observe(Said("u2", "s2", "dwa"));
        ruler.Observe(Said("u3", "s2", "trzy"));

        ruler.Jump(ruler.Ticks[1]);

        Assert.Equal("u2", landed);
    }

    [AvaloniaFact]
    public void ATranslationLandingLateDoesNotReopenAFinishedTurn()
    {
        var ruler = Ruler();

        ruler.Observe(Said("u1", "s1", "一"));
        ruler.Observe(Said("u2", "s1", "二"));
        ruler.Observe(Said("u3", "s2", "dwa"));

        // translations upsert the utterance they were asked for, long after the room moved on
        ruler.Observe(Said("u1", "s1", "一"));
        ruler.Observe(Said("u2", "s1", "二"));

        Assert.Equal(2, ruler.Ticks.Count);
        Assert.Equal(["u1", "u3"], ruler.Ticks.Select(t => t.AnchorUtteranceId));
        Assert.Equal([2, 1], ruler.Ticks.Select(t => t.Utterances));
    }

    [AvaloniaFact]
    public void OnlyTheNewestTickIsTheCurrentOne()
    {
        var ruler = Ruler();

        ruler.Observe(Said("u1", "s1", "一"));
        Assert.True(ruler.Ticks[0].IsCurrent);

        ruler.Observe(Said("u2", "s2", "dwa"));

        Assert.False(ruler.Ticks[0].IsCurrent);
        Assert.True(ruler.Ticks[1].IsCurrent);
    }

    [AvaloniaFact]
    public void ATickCarriesTheOffsetIntoTheMeetingItPointsAt()
    {
        var ruler = Ruler();

        ruler.Observe(Said("u1", "s1", "一", startMs: 4_000));
        ruler.Observe(Said("u2", "s2", "dwa", startMs: 128_000));

        // the first thing said is the meeting's zero, whatever the capture clock says
        Assert.Equal("0:00", ruler.Ticks[0].TimeLabel);
        Assert.Equal("2:04", ruler.Ticks[1].TimeLabel);
    }

    [AvaloniaFact]
    public void MoreTurnsThanSlotsFoldIntoStridedTicksWithoutLosingEitherEnd()
    {
        var ruler = Ruler();

        for (var i = 0; i < 200; i++)
            ruler.Observe(Said($"u{i}", i % 2 == 0 ? "s1" : "s2", $"turn {i}", startMs: i * 1000));

        Assert.True(ruler.Ticks.Count <= TranscriptRulerViewModel.Slots);
        // 200 turns fold by four; nothing is dropped, the strip just gets a coarser stride
        Assert.Equal(50, ruler.Ticks.Count);
        Assert.All(ruler.Ticks, t => Assert.Equal(4, t.Turns));
        Assert.Equal(200, ruler.Ticks.Sum(t => t.Turns));

        // both ends still reachable, and every tick lands on a real turn, in order
        Assert.Equal("u0", ruler.Ticks[0].AnchorUtteranceId);
        Assert.Equal("u196", ruler.Ticks[^1].AnchorUtteranceId);
        Assert.Equal(
            ruler.Ticks.Select(t => t.AnchorUtteranceId).ToList(),
            ruler.Ticks.Select(t => t.AnchorUtteranceId).Distinct().ToList());
    }

    [AvaloniaFact]
    public void TheStripNeverOverflowsWhileTheMeetingGrows()
    {
        var ruler = Ruler();

        for (var i = 0; i < 400; i++)
        {
            ruler.Observe(Said($"u{i}", i % 2 == 0 ? "s1" : "s2", $"turn {i}"));
            Assert.True(ruler.Ticks.Count <= TranscriptRulerViewModel.Slots);
        }

        Assert.NotEmpty(ruler.Ticks);
    }

    [AvaloniaFact]
    public void AFoldedTickSpanningTwoSpeakersSpendsNoSpeakerColour()
    {
        var ruler = Ruler();

        for (var i = 0; i < 200; i++)
            ruler.Observe(Said($"u{i}", i % 2 == 0 ? "s1" : "s2", $"turn {i}"));

        Assert.Equal(50, ruler.Ticks.Count);
        // colour identifies a person; a mark standing for four people's turns identifies nobody
        Assert.All(ruler.Ticks, t => Assert.Equal("#7C8A93", t.SpeakerColor));
        Assert.All(ruler.Ticks, t => Assert.Equal("S1, S2", t.SpeakerName));
    }

    [AvaloniaFact]
    public void MergingTwoSpeakersClosesTheSeamTheirTurnsLeftInTheRuler()
    {
        var ruler = Ruler();

        ruler.Observe(Said("u1", "s1", "一"));
        ruler.Observe(Said("u2", "s2", "二"));
        ruler.Observe(Said("u3", "s1", "三"));
        Assert.Equal(3, ruler.Ticks.Count);

        _canonical["s2"] = "s1";
        ruler.Reresolve();

        Assert.Single(ruler.Ticks);
        Assert.Equal("u1", ruler.Ticks[0].AnchorUtteranceId);
        Assert.Equal(3, ruler.Ticks[0].Utterances);
    }

    [AvaloniaFact]
    public void RenamingASpeakerRewritesEveryTickThatWasTheirs()
    {
        var ruler = Ruler();

        ruler.Observe(Said("u1", "s1", "一"));
        ruler.Observe(Said("u2", "s2", "dwa"));
        Assert.Equal("S1", ruler.Ticks[0].SpeakerName);

        _names["s1"] = "Wei";
        ruler.Reresolve();

        Assert.Equal("Wei", ruler.Ticks[0].SpeakerName);
        Assert.Equal("S2", ruler.Ticks[1].SpeakerName);
    }

    [AvaloniaFact]
    public void EveryTickIsStructuralUntilTheSemanticLayerLands()
    {
        var ruler = Ruler();

        ruler.Observe(Said("u1", "s1", "一"));
        ruler.Observe(Said("u2", "s2", "dwa"));

        Assert.Equal(2, ruler.Ticks.Count);
        Assert.All(ruler.Ticks, t => Assert.Equal(RulerTickKind.Speaker, t.Kind));
    }

    [AvaloniaFact]
    public void HoveringATickOffersItsPreviewAndLeavingWithdrawsIt()
    {
        var ruler = Ruler();

        ruler.Observe(Said("u1", "s1", "一"));
        ruler.Observe(Said("u2", "s2", "dwa"));
        Assert.Null(ruler.HoveredTick);

        ruler.Hover(ruler.Ticks[1]);

        Assert.Same(ruler.Ticks[1], ruler.HoveredTick);
        Assert.True(ruler.IsPreviewOpen);

        ruler.Hover(null);

        Assert.Null(ruler.HoveredTick);
        Assert.False(ruler.IsPreviewOpen);
    }

    [AvaloniaFact]
    public void ClearingForgetsTheMeetingSoTheNextOneStartsFromNothing()
    {
        var ruler = Ruler();

        ruler.Observe(Said("u1", "s1", "一", startMs: 60_000));
        ruler.Hover(ruler.Ticks[0]);

        ruler.Clear();

        Assert.Empty(ruler.Ticks);
        Assert.Null(ruler.HoveredTick);

        ruler.Observe(Said("v1", "s2", "dwa", startMs: 900_000));
        Assert.Equal("0:00", ruler.Ticks[0].TimeLabel);
    }

    [AvaloniaFact]
    public async Task ARunningMeetingLaysDownTicksAsItGoes()
    {
        var vm = TestViewModels.Demo();

        await vm.StartCommand.ExecuteAsync(null);
        await WaitForAsync(() => vm.Ruler.Ticks.Count >= 2);

        Assert.All(vm.Ruler.Ticks, t => Assert.NotEqual("", t.AnchorUtteranceId));
        Assert.All(vm.Ruler.Ticks, t => Assert.NotEqual("", t.Preview));
        Assert.True(vm.Ruler.Ticks[^1].IsCurrent);

        // a jump names an utterance the transcript body actually holds
        string? landed = null;
        vm.Ruler.JumpRequested += id => landed = id;
        vm.Ruler.Jump(vm.Ruler.Ticks[0]);
        Assert.Contains(vm.Columns[0].Bubbles, b => b.UtteranceId == landed);

        await vm.StopCommand.ExecuteAsync(null);
    }

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
}
