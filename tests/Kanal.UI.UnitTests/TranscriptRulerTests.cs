using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Kanal.Core.Models;
using Kanal.Host.ViewModels;

namespace Kanal.UI.UnitTests;

/// <summary>
/// The navigation ruler beside the transcript. Everything asserted here is the model behind the
/// marks — which ticks exist for a given run of utterances, what each one previews, where a click
/// lands, and how the pitch shrinks when a long meeting has more sentences than the strip has room.
/// Nothing here asserts a pixel; the strip's geometry is a XAML concern.
/// </summary>
public class TranscriptRulerTests
{
    private readonly Dictionary<string, string> _canonical = new();
    private readonly Dictionary<string, string> _names = new();

    private TranscriptRulerViewModel Ruler(TimeProvider? time = null) => new(tag =>
    {
        var canonical = _canonical.TryGetValue(tag, out var c) ? c : tag;
        var name = _names.TryGetValue(canonical, out var n) ? n : canonical.ToUpperInvariant();
        return (canonical, name, canonical == "s1" ? "#B23A2E" : "#1C6B58");
    }, time);

    private sealed class ManualTime : TimeProvider
    {
        private long _ticks;
        public override long GetTimestamp() => _ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public void Advance(TimeSpan by) => _ticks += by.Ticks;
    }

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
    public void EveryFinalSentenceGetsItsOwnTickWhoeverSaidIt()
    {
        var ruler = Ruler();

        ruler.Observe(Said("u1", "s1", "第一句"));
        ruler.Observe(Said("u2", "s1", "第二句"));
        ruler.Observe(Said("u3", "s2", "Trzecie zdanie"));

        Assert.Equal(["u1", "u2", "u3"], ruler.Ticks.Select(t => t.AnchorUtteranceId));
        Assert.Equal(["#B23A2E", "#B23A2E", "#1C6B58"], ruler.Ticks.Select(t => t.SpeakerColor));
    }

    [AvaloniaFact]
    public void ASentenceStillBeingSpokenHasNoTickUntilItIsFinal()
    {
        var ruler = Ruler();

        ruler.Observe(Said("u1", "s1", "我们把", state: UtteranceState.Partial));
        Assert.Empty(ruler.Ticks);

        ruler.Observe(Said("u1", "s1", "我们把 KX-4402 的公差改了"));

        Assert.Equal("我们把 KX-4402 的公差改了", Assert.Single(ruler.Ticks).Preview);
    }

    [AvaloniaFact]
    public void ATickFollowsLaterRevisionsOfItsSentence()
    {
        var ruler = Ruler();

        ruler.Observe(Said("u1", "s1", "我们把"));
        ruler.Observe(Said("u2", "s1", "下周交货"));
        ruler.Observe(Said("u1", "s2", "我们把公差改了"));

        Assert.Equal(["u1", "u2"], ruler.Ticks.Select(t => t.AnchorUtteranceId));
        Assert.Equal("我们把公差改了", ruler.Ticks[0].Preview);
        Assert.Equal("S2", ruler.Ticks[0].SpeakerName);
    }

    [AvaloniaFact]
    public void ClickingATickAsksToJumpToItsUtterance()
    {
        var ruler = Ruler();
        string? landed = null;
        ruler.JumpRequested += id => landed = id;

        ruler.Observe(Said("u1", "s1", "一"));
        ruler.Observe(Said("u2", "s2", "dwa"));
        ruler.Observe(Said("u3", "s2", "trzy"));

        ruler.Jump(ruler.Ticks[2]);

        Assert.Equal("u3", landed);
    }

    [AvaloniaFact]
    public void ATranslationLandingLateDoesNotAddAnotherTick()
    {
        var ruler = Ruler();

        ruler.Observe(Said("u1", "s1", "一"));
        ruler.Observe(Said("u2", "s1", "二"));
        ruler.Observe(Said("u3", "s2", "dwa"));

        ruler.Observe(Said("u1", "s1", "一"));
        ruler.Observe(Said("u2", "s1", "二"));

        Assert.Equal(["u1", "u2", "u3"], ruler.Ticks.Select(t => t.AnchorUtteranceId));
    }

    [AvaloniaFact]
    public void OnlyTheSentencesOnScreenAreMarkedInView()
    {
        var ruler = Ruler();
        for (var i = 0; i < 6; i++)
            ruler.Observe(Said($"u{i}", "s1", $"line {i}"));
        Assert.All(ruler.Ticks, t => Assert.False(t.IsInView));

        ruler.SetVisible(["u2", "u3", "partial"]);

        Assert.Equal(["u2", "u3"], ruler.Ticks.Where(t => t.IsInView).Select(t => t.AnchorUtteranceId));

        ruler.SetVisible(["u5"]);

        Assert.Equal(["u5"], ruler.Ticks.Where(t => t.IsInView).Select(t => t.AnchorUtteranceId));
    }

    [AvaloniaFact]
    public void ATickOnScreenReachesFurtherThanOneOffScreen()
    {
        var ruler = Ruler();
        ruler.Observe(Said("u0", "s1", "一"));
        ruler.Observe(Said("u1", "s1", "二"));

        ruler.SetVisible(["u1"]);

        Assert.True(ruler.Ticks[1].Reach > ruler.Ticks[0].Reach);
    }

    private TranscriptRulerViewModel NineTicks()
    {
        var ruler = Ruler();
        for (var i = 0; i < 9; i++)
            ruler.Observe(Said($"u{i}", "s1", $"line {i}"));
        return ruler;
    }

    private static double CentreOf(TranscriptRulerViewModel ruler, int index) =>
        index * ruler.Ticks[index].Pitch + ruler.Ticks[index].Pitch / 2;

    [AvaloniaFact]
    public void AllTicksAtRestAreThinAndDim()
    {
        var ruler = NineTicks();

        Assert.All(ruler.Ticks, t => Assert.Equal(TranscriptRulerViewModel.RestThickness, t.Thickness));
        Assert.All(ruler.Ticks, t => Assert.Equal(TranscriptRulerViewModel.RestGlow, t.Glow));
    }

    [AvaloniaFact]
    public void ThePointerRaisesTheTickUnderItToLongestThickestAndBrightest()
    {
        var ruler = NineTicks();

        ruler.PointerAt(CentreOf(ruler, 4));

        Assert.Equal(TranscriptRulerViewModel.PeakReach, ruler.Ticks[4].Reach, 6);
        Assert.Equal(TranscriptRulerViewModel.PeakThickness, ruler.Ticks[4].Thickness, 6);
        Assert.Equal(1, ruler.Ticks[4].Glow, 6);
    }

    [AvaloniaFact]
    public void TheRaiseFallsOffSmoothlyAndEquallyOnBothSidesOfThePointer()
    {
        var ruler = NineTicks();

        ruler.PointerAt(CentreOf(ruler, 4));

        var reach = ruler.Ticks.Select(t => t.Reach).ToList();
        Assert.True(reach[4] > reach[5] && reach[5] > reach[6] && reach[6] > reach[7] && reach[7] > reach[8]);
        Assert.True(reach[8] > TranscriptRulerViewModel.RestReach);
        Assert.Equal(reach[3], reach[5], 6);
        Assert.Equal(reach[2], reach[6], 6);
        Assert.True(ruler.Ticks[5].Glow < 1 && ruler.Ticks[5].Glow > ruler.Ticks[8].Glow);
        Assert.True(ruler.Ticks[5].Thickness < ruler.Ticks[4].Thickness
            && ruler.Ticks[5].Thickness > ruler.Ticks[8].Thickness);
    }

    [AvaloniaFact]
    public void TheTickUnderThePointerIsAtLeastFortyDipLong()
    {
        var ruler = NineTicks();

        ruler.PointerAt(CentreOf(ruler, 4));

        Assert.True(ruler.Ticks[4].Reach >= 40, $"the tick under the pointer is only {ruler.Ticks[4].Reach:F1} DIP long");
    }

    [AvaloniaFact]
    public void ThePeakIsSharperThanAGaussianAndTheTailHeavier()
    {
        var ruler = NineTicks();

        ruler.PointerAt(CentreOf(ruler, 4));

        double Rise(int index) => ruler.Ticks[index].Reach - TranscriptRulerViewModel.RestReach;
        Assert.True(Rise(5) < 0.70 * Rise(4), $"the next tick keeps {Rise(5) / Rise(4):P0} of the rise; a Gaussian keeps 78%");
        Assert.True(Rise(8) > 0.05 * Rise(4), $"four ticks away only {Rise(8) / Rise(4):P1} of the rise is left; a Gaussian leaves 1.8%");
    }

    [AvaloniaFact]
    public void APointerBetweenTwoTicksRaisesBothAlike()
    {
        var ruler = NineTicks();
        var seam = ruler.Ticks[3].Pitch * 4;

        ruler.PointerAt(seam);

        Assert.Equal(ruler.Ticks[3].Reach, ruler.Ticks[4].Reach, 6);
        Assert.Equal(ruler.Ticks[3].Glow, ruler.Ticks[4].Glow, 6);
        Assert.True(ruler.Ticks[3].Reach < TranscriptRulerViewModel.PeakReach);
    }

    [AvaloniaFact]
    public void MovingThePointerAcrossATickGrowsItWithoutAJump()
    {
        var ruler = NineTicks();
        var pitch = ruler.Ticks[0].Pitch;
        var reach = new List<double>();

        for (var step = 0; step <= 8; step++)
        {
            ruler.PointerAt(CentreOf(ruler, 2) - pitch + step * pitch / 4);
            reach.Add(ruler.Ticks[2].Reach);
        }

        Assert.Equal(TranscriptRulerViewModel.PeakReach, reach[4], 6);
        Assert.True(reach[0] < reach[1] && reach[1] < reach[2] && reach[2] < reach[3] && reach[3] < reach[4]);
        Assert.True(reach[4] > reach[5] && reach[5] > reach[6] && reach[6] > reach[7] && reach[7] > reach[8]);
        Assert.All(reach.Zip(reach.Skip(1)), pair =>
            Assert.True(Math.Abs(pair.Second - pair.First) < 3.5, "a quarter pitch of travel must not jump a tick"));
    }

    [AvaloniaFact]
    public void ATickFarFromThePointerStaysAtRest()
    {
        var ruler = Ruler();
        for (var i = 0; i < 40; i++)
            ruler.Observe(Said($"u{i}", "s1", $"line {i}"));

        ruler.PointerAt(CentreOf(ruler, 5));

        Assert.Equal(TranscriptRulerViewModel.RestReach, ruler.Ticks[39].Reach);
        Assert.Equal(TranscriptRulerViewModel.RestThickness, ruler.Ticks[39].Thickness);
        Assert.Equal(TranscriptRulerViewModel.RestGlow, ruler.Ticks[39].Glow);
    }

    [AvaloniaFact]
    public void LeavingTheRulerReturnsEveryTickToRest()
    {
        var ruler = NineTicks();
        ruler.PointerAt(CentreOf(ruler, 4));

        ruler.PointerAt(null);

        Assert.All(ruler.Ticks, t => Assert.Equal(TranscriptRulerViewModel.RestReach, t.Reach));
        Assert.All(ruler.Ticks, t => Assert.Equal(TranscriptRulerViewModel.RestThickness, t.Thickness));
        Assert.All(ruler.Ticks, t => Assert.Equal(TranscriptRulerViewModel.RestGlow, t.Glow));
    }

    [AvaloniaFact]
    public void TicksOnScreenKeepTheirEmphasisAtRestAndTheRaiseNeverShortensThem()
    {
        var ruler = NineTicks();
        ruler.SetVisible(["u6"]);
        var onScreen = ruler.Ticks[6];

        Assert.Equal(TranscriptRulerViewModel.InViewReach, onScreen.Reach);
        Assert.True(onScreen.Thickness > TranscriptRulerViewModel.RestThickness);
        Assert.True(onScreen.Glow > TranscriptRulerViewModel.RestGlow);

        ruler.PointerAt(CentreOf(ruler, 0));

        Assert.True(onScreen.Reach >= TranscriptRulerViewModel.InViewReach);
        Assert.True(onScreen.Thickness >= TranscriptRulerViewModel.InViewThickness);
        Assert.True(onScreen.Glow >= TranscriptRulerViewModel.InViewGlow);
    }

    [AvaloniaFact]
    public void ARulerThatChangesSizeKeepsTheRaiseUnderThePointer()
    {
        var ruler = Ruler();
        for (var i = 0; i < 200; i++)
            ruler.Observe(Said($"u{i}", "s1", $"line {i}"));
        ruler.PointerAt(40);

        ruler.SetStripHeight(300);

        var hot = ruler.Ticks.Select((t, i) => (t.Reach, i)).MaxBy(x => x.Reach).i;
        Assert.InRange(CentreOf(ruler, hot), 40 - ruler.Ticks[0].Pitch, 40 + ruler.Ticks[0].Pitch);
    }

    [AvaloniaFact]
    public void AMoveOfThePointerNotifiesEachTickOnceSoOneTransitionRestartsPerTick()
    {
        var ruler = Ruler();
        for (var i = 0; i < 40; i++)
            ruler.Observe(Said($"u{i}", "s1", $"line {i}"));
        var changes = new Dictionary<string, int>();
        foreach (var tick in ruler.Ticks)
            tick.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == "Shape")
                    changes[tick.AnchorUtteranceId] = changes.GetValueOrDefault(tick.AnchorUtteranceId) + 1;
            };

        ruler.PointerAt(CentreOf(ruler, 20));

        Assert.Equal(ruler.Ticks.Count(t => t.Reach > TranscriptRulerViewModel.RestReach), changes.Count);
        Assert.All(changes.Values, count => Assert.Equal(1, count));
    }

    private static TranscriptRulerViewModel TwentyOneTicks(TranscriptRulerViewModel ruler, Func<string, int, Utterance> said)
    {
        for (var i = 0; i < 21; i++)
            ruler.Observe(said($"u{i}", i));
        return ruler;
    }

    private (TranscriptRulerViewModel Ruler, ManualTime Time) RulerAfterAMove(double dipPerSecond, int direction)
    {
        var time = new ManualTime();
        var ruler = TwentyOneTicks(Ruler(time), (id, i) => Said(id, "s1", $"line {i}"));
        var y = CentreOf(ruler, 10) - direction * 10 * dipPerSecond * 0.02;
        ruler.PointerAt(y);
        for (var step = 0; step < 10; step++)
        {
            time.Advance(TimeSpan.FromMilliseconds(20));
            y += direction * dipPerSecond * 0.02;
            ruler.PointerAt(y);
        }

        return (ruler, time);
    }

    private static (double Behind, double Ahead) TwoTicksFromThePointer(TranscriptRulerViewModel ruler, int direction)
    {
        var pointer = ruler.Ticks.Select((t, i) => (t.Reach, i)).MaxBy(x => x.Reach).i;
        return (ruler.Ticks[pointer - 2 * direction].Reach, ruler.Ticks[pointer + 2 * direction].Reach);
    }

    [AvaloniaFact]
    public void MovingDownLeavesALongTailAboveAndASharpFrontBelow()
    {
        var (ruler, _) = RulerAfterAMove(dipPerSecond: 450, direction: 1);

        var (behind, ahead) = TwoTicksFromThePointer(ruler, 1);

        Assert.True(behind > ahead + 3, $"behind {behind:F1} should be well above ahead {ahead:F1}");
        Assert.True(ahead > TranscriptRulerViewModel.RestReach);
    }

    [AvaloniaFact]
    public void MovingUpMirrorsIt()
    {
        var (ruler, _) = RulerAfterAMove(dipPerSecond: 450, direction: -1);

        var (behind, ahead) = TwoTicksFromThePointer(ruler, -1);

        Assert.True(behind > ahead + 3, $"behind {behind:F1} should be well above ahead {ahead:F1}");
    }

    [AvaloniaFact]
    public void TheTickNearestThePointerIsAlwaysTheLongestWhateverTheSpeed()
    {
        var (ruler, _) = RulerAfterAMove(dipPerSecond: 900, direction: 1);

        var pointer = ruler.Ticks.Select((t, i) => (t.Reach, i)).MaxBy(x => x.Reach);

        Assert.Equal(TranscriptRulerViewModel.PeakReach, pointer.Reach, 3);
    }

    [AvaloniaFact]
    public void ASlowMoveBarelySkewsTheBump()
    {
        var (ruler, _) = RulerAfterAMove(dipPerSecond: 10, direction: 1);

        var (behind, ahead) = TwoTicksFromThePointer(ruler, 1);

        Assert.InRange(behind - ahead, -1, 1);
    }

    [AvaloniaFact]
    public void WhenThePointerStopsTheBumpSettlesToASymmetricBell()
    {
        var (ruler, time) = RulerAfterAMove(dipPerSecond: 450, direction: 1);
        Assert.True(ruler.IsSkewed);

        for (var i = 0; i < 40; i++)
        {
            time.Advance(TimeSpan.FromMilliseconds(16));
            ruler.Relax();
        }

        var (behind, ahead) = TwoTicksFromThePointer(ruler, 1);
        Assert.False(ruler.IsSkewed);
        Assert.Equal(behind, ahead, 1);
    }

    [AvaloniaFact]
    public void AStillPointerKeepsAnEvenBumpEvenWhenEventsKeepArriving()
    {
        var time = new ManualTime();
        var ruler = TwentyOneTicks(Ruler(time), (id, i) => Said(id, "s1", $"line {i}"));
        var y = CentreOf(ruler, 10);

        for (var i = 0; i < 20; i++)
        {
            time.Advance(TimeSpan.FromMilliseconds(16));
            ruler.PointerAt(y);
        }

        Assert.False(ruler.IsSkewed);
        Assert.Equal(ruler.Ticks[8].Reach, ruler.Ticks[12].Reach, 6);
    }

    [AvaloniaFact]
    public void LeavingTheRulerForgetsTheSkew()
    {
        var (ruler, time) = RulerAfterAMove(dipPerSecond: 450, direction: 1);

        ruler.PointerAt(null);
        time.Advance(TimeSpan.FromMilliseconds(16));
        ruler.PointerAt(CentreOf(ruler, 10));

        Assert.False(ruler.IsSkewed);
        Assert.Equal(ruler.Ticks[8].Reach, ruler.Ticks[12].Reach, 6);
    }

    private (TranscriptRulerViewModel Ruler, ManualTime Time) RulerWithTenTicks()
    {
        var time = new ManualTime();
        var ruler = Ruler(time);
        for (var i = 0; i < 10; i++)
            ruler.Observe(Said($"u{i}", "s1", $"line {i}"));
        return (ruler, time);
    }

    private static void Frames(TranscriptRulerViewModel ruler, ManualTime time, int count)
    {
        for (var i = 0; i < count; i++)
        {
            time.Advance(TimeSpan.FromMilliseconds(16));
            ruler.Relax();
        }
    }

    [AvaloniaFact]
    public void ATickIsDrawnAtRestUntilAFrameMovesItTowardsItsTarget()
    {
        var (ruler, time) = RulerWithTenTicks();

        ruler.PointerAt(CentreOf(ruler, 4));

        Assert.Equal(TranscriptRulerViewModel.PeakReach, ruler.Ticks[4].Reach, 6);
        Assert.Equal(TranscriptRulerViewModel.RestReach, ruler.Ticks[4].DrawnShape.Reach);
        Assert.True(ruler.IsSettling);

        Frames(ruler, time, 1);
        var first = ruler.Ticks[4].DrawnShape.Reach;
        Frames(ruler, time, 1);
        var second = ruler.Ticks[4].DrawnShape.Reach;

        Assert.True(first > TranscriptRulerViewModel.RestReach);
        Assert.True(second > first);
        Assert.True(second < TranscriptRulerViewModel.PeakReach);
    }

    [AvaloniaFact]
    public void TheTickUnderThePointerIsMostlyThereWithinTwoFrames()
    {
        var (ruler, time) = RulerWithTenTicks();
        ruler.PointerAt(CentreOf(ruler, 4));

        Frames(ruler, time, 2);

        var reach = ruler.Ticks[4].DrawnShape.Reach;
        var share = (reach - TranscriptRulerViewModel.RestReach)
            / (TranscriptRulerViewModel.PeakReach - TranscriptRulerViewModel.RestReach);
        Assert.True(share > 0.7, $"after two frames the tick is only {share:P0} of the way");
    }

    [AvaloniaFact]
    public void ATickIsDrawnAtItsTargetOnceTheFramesHaveRun()
    {
        var (ruler, time) = RulerWithTenTicks();
        ruler.PointerAt(CentreOf(ruler, 4));

        Frames(ruler, time, 40);

        Assert.False(ruler.IsSettling);
        Assert.All(ruler.Ticks, t => Assert.Equal(t.Shape, t.DrawnShape));
        Assert.All(ruler.Ticks, t => Assert.Equal(t.Glow, t.DrawnGlow, 6));
    }

    [AvaloniaFact]
    public void ATickGrowsFasterThanItShrinks()
    {
        var (ruler, time) = RulerWithTenTicks();
        ruler.PointerAt(CentreOf(ruler, 4));
        Frames(ruler, time, 3);
        var grown = ruler.Ticks[4].DrawnShape.Reach - TranscriptRulerViewModel.RestReach;

        Frames(ruler, time, 40);
        ruler.PointerAt(null);
        Frames(ruler, time, 3);
        var shrunk = TranscriptRulerViewModel.PeakReach - ruler.Ticks[4].DrawnShape.Reach;

        Assert.True(grown > shrunk, $"grew {grown:F1} but shrank {shrunk:F1} in the same three frames");
    }

    [AvaloniaFact]
    public void LeavingTheRulerEasesTheDrawnTicksBackToRestInsteadOfSnapping()
    {
        var (ruler, time) = RulerWithTenTicks();
        ruler.PointerAt(CentreOf(ruler, 4));
        Frames(ruler, time, 40);

        ruler.PointerAt(null);

        Assert.Equal(TranscriptRulerViewModel.RestReach, ruler.Ticks[4].Reach);
        Assert.Equal(TranscriptRulerViewModel.PeakReach, ruler.Ticks[4].DrawnShape.Reach, 6);
        Assert.True(ruler.IsSettling);

        Frames(ruler, time, 60);

        Assert.Equal(TranscriptRulerViewModel.RestReach, ruler.Ticks[4].DrawnShape.Reach, 6);
        Assert.False(ruler.IsSettling);
    }

    [AvaloniaFact]
    public void AChangeOfWhatIsOnScreenIsDrawnAtOnce()
    {
        var (ruler, _) = RulerWithTenTicks();

        ruler.SetVisible(["u6"]);

        Assert.Equal(TranscriptRulerViewModel.InViewReach, ruler.Ticks[6].DrawnShape.Reach);
        Assert.Equal(TranscriptRulerViewModel.InViewGlow, ruler.Ticks[6].DrawnGlow, 6);
        Assert.False(ruler.IsSettling);
    }

    [AvaloniaFact]
    public void AFastMoveKeepsTheTickUnderThePointerCloseToItsPeakWhileTheTailTrails()
    {
        var time = new ManualTime();
        var ruler = TwentyOneTicks(Ruler(time), (id, i) => Said(id, "s1", $"line {i}"));
        var y = CentreOf(ruler, 2);
        ruler.PointerAt(y);

        for (var step = 0; step < 12; step++)
        {
            time.Advance(TimeSpan.FromMilliseconds(16));
            y += 8;
            ruler.PointerAt(y);
            ruler.Relax();
        }

        var under = ruler.Ticks.Select((t, i) => (t.DrawnShape.Reach, i)).MaxBy(x => x.Reach);
        Assert.True(under.Reach > 20, $"the tick nearest the pointer is drawn at only {under.Reach:F1}");
        Assert.True(Math.Abs(CentreOf(ruler, under.i) - y) < 3 * ruler.Ticks[0].Pitch,
            "the drawn peak must stay within three ticks of the pointer");
    }

    [AvaloniaFact]
    public void TheUpArrowPagesToTheLastSentenceAboveTheScreen()
    {
        var ruler = Ruler();
        (string Id, PageDirection Direction)? paged = null;
        ruler.PageRequested += (id, direction) => paged = (id, direction);
        for (var i = 0; i < 8; i++)
            ruler.Observe(Said($"u{i}", "s1", $"line {i}"));

        ruler.SetVisible(["u3", "u4"]);
        ruler.PageUpCommand.Execute(null);

        Assert.Equal(("u2", PageDirection.Up), paged);
    }

    [AvaloniaFact]
    public void TheDownArrowPagesToTheFirstSentenceBelowTheScreen()
    {
        var ruler = Ruler();
        (string Id, PageDirection Direction)? paged = null;
        ruler.PageRequested += (id, direction) => paged = (id, direction);
        for (var i = 0; i < 8; i++)
            ruler.Observe(Said($"u{i}", "s1", $"line {i}"));

        ruler.SetVisible(["u3", "u4"]);
        ruler.PageDownCommand.Execute(null);

        Assert.Equal(("u5", PageDirection.Down), paged);
    }

    [AvaloniaFact]
    public void ASentenceCutByTheEdgeOfTheScreenIsTheNextPageNotSkipped()
    {
        var ruler = Ruler();
        var paged = new List<(string, PageDirection)>();
        ruler.PageRequested += (id, direction) => paged.Add((id, direction));
        for (var i = 0; i < 8; i++)
            ruler.Observe(Said($"u{i}", "s1", $"line {i}"));

        ruler.SetVisible(["u2", "u3", "u4", "u5"], whole: ["u3", "u4"]);
        ruler.PageUpCommand.Execute(null);
        ruler.PageDownCommand.Execute(null);

        Assert.Equal([("u2", PageDirection.Up), ("u5", PageDirection.Down)], paged);
        Assert.Equal(["u2", "u3", "u4", "u5"], ruler.Ticks.Where(t => t.IsInView).Select(t => t.AnchorUtteranceId));
    }

    [AvaloniaFact]
    public void ASentenceTallerThanTheScreenDoesNotTrapTheArrows()
    {
        var ruler = Ruler();
        var paged = new List<(string, PageDirection)>();
        ruler.PageRequested += (id, direction) => paged.Add((id, direction));
        for (var i = 0; i < 5; i++)
            ruler.Observe(Said($"u{i}", "s1", $"line {i}"));

        ruler.SetVisible(["u2"], whole: []);
        ruler.PageUpCommand.Execute(null);
        ruler.PageDownCommand.Execute(null);

        Assert.Equal([("u1", PageDirection.Up), ("u3", PageDirection.Down)], paged);
    }

    [AvaloniaFact]
    public void TwoCutSentencesAndNothingWholeArePagedToNotSkipped()
    {
        var ruler = Ruler();
        var paged = new List<(string, PageDirection)>();
        ruler.PageRequested += (id, direction) => paged.Add((id, direction));
        for (var i = 0; i < 5; i++)
            ruler.Observe(Said($"u{i}", "s1", $"line {i}"));

        ruler.SetVisible(["u2", "u3"], whole: []);
        ruler.PageUpCommand.Execute(null);
        ruler.PageDownCommand.Execute(null);

        Assert.Equal([("u2", PageDirection.Up), ("u3", PageDirection.Down)], paged);
    }

    [AvaloniaFact]
    public void AnArrowIsOfferedOnlyWhenSomethingIsOffScreenThatWay()
    {
        var ruler = Ruler();
        Assert.False(ruler.PageUpCommand.CanExecute(null));
        Assert.False(ruler.PageDownCommand.CanExecute(null));

        ruler.Observe(Said("u0", "s1", "一"));
        ruler.Observe(Said("u1", "s1", "二"));
        ruler.SetVisible(["u0", "u1"]);
        Assert.False(ruler.PageUpCommand.CanExecute(null));
        Assert.False(ruler.PageDownCommand.CanExecute(null));

        ruler.Observe(Said("u2", "s1", "三"));
        Assert.True(ruler.PageDownCommand.CanExecute(null));

        ruler.SetVisible(["u2"]);
        Assert.True(ruler.PageUpCommand.CanExecute(null));
        Assert.False(ruler.PageDownCommand.CanExecute(null));
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
    public void AShortMeetingKeepsTheFixedPitch()
    {
        var ruler = Ruler();

        for (var i = 0; i < 10; i++)
            ruler.Observe(Said($"u{i}", "s1", $"line {i}"));

        Assert.All(ruler.Ticks, t => Assert.Equal(TranscriptRulerViewModel.MaxPitch, t.Pitch));
    }

    [AvaloniaFact]
    public void ALongMeetingKeepsEverySentenceAndSharesTheStripEvenly()
    {
        var ruler = Ruler();

        for (var i = 0; i < 600; i++)
            ruler.Observe(Said($"u{i}", i % 2 == 0 ? "s1" : "s2", $"line {i}", startMs: i * 1000));

        Assert.Equal(600, ruler.Ticks.Count);
        Assert.Equal("u0", ruler.Ticks[0].AnchorUtteranceId);
        Assert.Equal("u599", ruler.Ticks[^1].AnchorUtteranceId);
        Assert.All(ruler.Ticks, t => Assert.Equal(TranscriptRulerViewModel.DefaultStripHeight / 600, t.Pitch));
        Assert.Equal(TranscriptRulerViewModel.DefaultStripHeight, ruler.Ticks.Sum(t => t.Pitch), 6);
        Assert.Equal(["#B23A2E", "#1C6B58"], ruler.Ticks.Take(2).Select(t => t.SpeakerColor));
    }

    [AvaloniaFact]
    public void ThePreviewCardStaysInsideTheStripAtBothEnds()
    {
        var ruler = Ruler();

        for (var i = 0; i < 300; i++)
            ruler.Observe(Said($"u{i}", "s1", $"line {i}"));

        ruler.Hover(ruler.Ticks[0]);
        Assert.Equal(0, ruler.PreviewTop);

        ruler.Hover(ruler.Ticks[^1]);
        Assert.Equal(TranscriptRulerViewModel.DefaultStripHeight - TranscriptRulerViewModel.CardHeight, ruler.PreviewTop);
    }

    [AvaloniaFact]
    public void TheRowsNeverAddUpToMoreThanTheStripThatIsReallyThere()
    {
        var ruler = Ruler();
        ruler.SetStripHeight(633);

        for (var i = 0; i < 165; i++)
        {
            ruler.Observe(Said($"u{i}", "s1", $"line {i}"));
            Assert.True(ruler.Ticks.Sum(t => t.Pitch) <= 633 + 1e-9);
        }

        Assert.Equal(633.0 / 165, ruler.Ticks[0].Pitch, 9);
    }

    [AvaloniaFact]
    public void AStripThatShrinksAfterwardsReSpacesTheTicksAndKeepsThePreviewInside()
    {
        var ruler = Ruler();
        for (var i = 0; i < 200; i++)
            ruler.Observe(Said($"u{i}", "s1", $"line {i}"));

        ruler.SetStripHeight(400);

        Assert.True(ruler.Ticks.Sum(t => t.Pitch) <= 400 + 1e-9);
        ruler.Hover(ruler.Ticks[^1]);
        Assert.Equal(400 - TranscriptRulerViewModel.CardHeight, ruler.PreviewTop);
    }

    [AvaloniaFact]
    public void MergingTwoSpeakersRelabelsTheirTicksInTheRuler()
    {
        var ruler = Ruler();

        ruler.Observe(Said("u1", "s1", "一"));
        ruler.Observe(Said("u2", "s2", "二"));
        ruler.Observe(Said("u3", "s1", "三"));

        _canonical["s2"] = "s1";
        ruler.Reresolve();

        Assert.Equal(3, ruler.Ticks.Count);
        Assert.All(ruler.Ticks, t => Assert.Equal("S1", t.SpeakerName));
        Assert.All(ruler.Ticks, t => Assert.Equal("#B23A2E", t.SpeakerColor));
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
        ruler.Observe(Said("u2", "s1", "二", startMs: 61_000));
        ruler.SetVisible(["u2"]);
        ruler.Hover(ruler.Ticks[0]);

        ruler.Clear();

        Assert.Empty(ruler.Ticks);
        Assert.Null(ruler.HoveredTick);
        Assert.False(ruler.PageUpCommand.CanExecute(null));

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

        // a jump names an utterance the transcript body actually holds
        string? landed = null;
        vm.Ruler.JumpRequested += id => landed = id;
        vm.Ruler.Jump(vm.Ruler.Ticks[0]);
        Assert.Contains(vm.Columns[0].Bubbles, b => b.UtteranceId == landed);

        await vm.StopCommand.ExecuteAsync(null);
    }

    [AvaloniaFact]
    public async Task AJumpMarksTheSentenceItLandsOnInEveryColumnAndTheNextJumpMovesTheMark()
    {
        var vm = TestViewModels.Demo();

        await vm.StartCommand.ExecuteAsync(null);
        await WaitForAsync(() => vm.Ruler.Ticks.Count >= 2);
        var first = vm.Ruler.Ticks[0].AnchorUtteranceId;
        var second = vm.Ruler.Ticks[1].AnchorUtteranceId;
        await WaitForAsync(() => vm.Columns.All(c =>
            c.Bubbles.Any(b => b.UtteranceId == second)));

        Assert.All(vm.Columns, c => Assert.DoesNotContain(c.Bubbles, b => b.IsJumpTarget));

        vm.Ruler.Jump(vm.Ruler.Ticks[0]);
        AssertMarked(vm, first);

        vm.Ruler.Jump(vm.Ruler.Ticks[1]);
        AssertMarked(vm, second);

        await vm.StopCommand.ExecuteAsync(null);
    }

    [AvaloniaFact]
    public async Task TheNextRoomStartsWithNothingMarked()
    {
        var vm = TestViewModels.Demo();

        await vm.StartCommand.ExecuteAsync(null);
        await WaitForAsync(() => vm.Ruler.Ticks.Count >= 1);
        var anchor = vm.Ruler.Ticks[0].AnchorUtteranceId;
        await WaitForAsync(() => vm.Columns.All(c => c.Bubbles.Any(b => b.UtteranceId == anchor)));
        vm.Ruler.Jump(vm.Ruler.Ticks[0]);
        AssertMarked(vm, anchor);
        await vm.StopCommand.ExecuteAsync(null);

        await vm.StartCommand.ExecuteAsync(null);
        await WaitForAsync(() => vm.Columns.Count > 0 && vm.Columns.All(c => c.Bubbles.Count > 0));

        Assert.All(vm.Columns, c => Assert.DoesNotContain(c.Bubbles, b => b.IsJumpTarget));
        await vm.StopCommand.ExecuteAsync(null);
    }

    [AvaloniaFact]
    public void AJumpToASentenceAColumnDoesNotHoldClearsThatColumnsMark()
    {
        var column = new ColumnViewModel("de");
        var marked = column.GetOrAdd("u1");
        column.GetOrAdd("u2");
        column.MarkJumpTarget("u1");

        column.MarkJumpTarget("not-in-this-column");

        Assert.False(marked.IsJumpTarget);
        Assert.DoesNotContain(column.Bubbles, b => b.IsJumpTarget);
    }

    private static void AssertMarked(MainViewModel vm, string utteranceId)
    {
        Assert.NotEmpty(vm.Columns);
        foreach (var column in vm.Columns)
            Assert.Equal(
                [utteranceId],
                column.Bubbles.Where(b => b.IsJumpTarget).Select(b => b.UtteranceId));
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
