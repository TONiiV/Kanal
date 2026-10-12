using System.Collections.Concurrent;
using Kanal.Core.Insights;
using Kanal.Core.Providers;
using Kanal.Core.Room;

namespace Kanal.Core.UnitTests;

public class MeetingAgentRunnerTests
{
    private static readonly TimeSpan Never = TimeSpan.FromHours(1);

    private static RoomState Room() => new(new RoomConfig("room", ["zh", "de"]));

    private static void Say(RoomState room, string id, bool final = true) =>
        room.ApplyTranscript(new AsrEvent.Transcript(
            id, "S01", $"said {id}", "zh", 0, 1000, IsFinal: final, CodeSwitch: false, 0.9, null));

    private static MeetingInsight Insight(InsightKind kind, string topic, params string[] sources) =>
        new("agent-chosen", kind, topic, $"about {topic}", sources);

    private static async Task Until(Func<bool> condition)
    {
        var deadline = Environment.TickCount64 + 5_000;
        while (!condition())
        {
            if (Environment.TickCount64 > deadline)
                throw new TimeoutException("Condition not met in time.");
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task AFullBatchOfFinalsReachesTheAgentInOneWindowInSpokenOrder()
    {
        var room = Room();
        var agent = new FakeMeetingAgent(_ => []);
        await using var runner = new MeetingAgentRunner(room, agent, _ => { }, "de", batch: 3, quiet: Never);

        Say(room, "u1");
        Say(room, "u2");
        Say(room, "u3");

        await Until(() => !agent.Windows.IsEmpty);
        var window = Assert.Single(agent.Windows);
        Assert.Equal(["u1", "u2", "u3"], window.Finals.Select(u => u.Id));
        Assert.Equal("de", window.ReplyLanguage);
    }

    [Fact]
    public async Task PartialsAndTheTranslationOfAFinalAreNotSentAgain()
    {
        var room = Room();
        var agent = new FakeMeetingAgent(_ => []);
        await using var runner = new MeetingAgentRunner(room, agent, _ => { }, "de", batch: 2, quiet: Never);

        Say(room, "u1", final: false);
        Say(room, "u1");
        room.ApplyTranslations("u1", 2, new Dictionary<string, string> { ["de"] = "gesagt" });
        Say(room, "u2");

        await Until(() => !agent.Windows.IsEmpty);
        Assert.Equal(["u1", "u2"], Assert.Single(agent.Windows).Finals.Select(u => u.Id));
    }

    [Fact]
    public async Task WhenTheRoomFallsQuietTheAgentGetsWhatWasSaidSoFar()
    {
        var room = Room();
        var agent = new FakeMeetingAgent(_ => []);
        await using var runner = new MeetingAgentRunner(
            room, agent, _ => { }, "de", batch: 100, quiet: TimeSpan.FromMilliseconds(50));

        Say(room, "u1");

        await Until(() => !agent.Windows.IsEmpty);
        Assert.Equal(["u1"], Assert.Single(agent.Windows).Finals.Select(u => u.Id));
    }

    [Fact]
    public async Task ACandidateThatCitesNothingSaidIsRefusedAndTheRestArriveAsCandidates()
    {
        var room = Room();
        var insights = new MeetingInsights(room);
        var proposed = 0;
        var agent = new FakeMeetingAgent(_ =>
        [
            Insight(InsightKind.Decision, "Delivery", "u404"),
            Insight(InsightKind.Point, "Tolerance", "u1") with { State = InsightState.Confirmed },
        ]);
        await using var runner = new MeetingAgentRunner(
            room, agent, insight => { insights.Record(insight); proposed++; }, "de", batch: 1, quiet: Never);

        Say(room, "u1");

        await Until(() => proposed == 2);
        var recorded = Assert.Single(insights.Items);
        Assert.Equal("Tolerance", recorded.Topic);
        Assert.Equal(InsightState.Candidate, recorded.State);
        Assert.NotEqual("agent-chosen", recorded.Id);
    }

    [Fact]
    public async Task EveryCandidateGetsItsOwnId()
    {
        var room = Room();
        var proposed = new ConcurrentQueue<MeetingInsight>();
        var agent = new FakeMeetingAgent(_ =>
            [Insight(InsightKind.Point, "A", "u1"), Insight(InsightKind.Point, "B", "u1")]);
        await using var runner = new MeetingAgentRunner(room, agent, proposed.Enqueue, "de", batch: 1, quiet: Never);

        Say(room, "u1");

        await Until(() => proposed.Count == 2);
        Assert.Equal(2, proposed.Select(i => i.Id).Distinct().Count());
    }

    [Fact]
    public async Task AKindTheAgentDidNotDeclareIsDropped()
    {
        var room = Room();
        var proposed = new ConcurrentQueue<MeetingInsight>();
        var agent = new FakeMeetingAgent(
            _ => [Insight(InsightKind.Decision, "Delivery", "u1"), Insight(InsightKind.Point, "Tolerance", "u1")],
            InsightKind.Point);
        await using var runner = new MeetingAgentRunner(room, agent, proposed.Enqueue, "de", batch: 1, quiet: Never);

        Say(room, "u1");

        await Until(() => !agent.Windows.IsEmpty);
        await Until(() => !proposed.IsEmpty);
        Assert.Equal(InsightKind.Point, Assert.Single(proposed).Kind);
    }

    [Fact]
    public async Task AnAgentThatFailsLosesOneWindowAndTheMeetingGoesOn()
    {
        var room = Room();
        var proposed = new ConcurrentQueue<MeetingInsight>();
        var calls = 0;
        var agent = new FakeMeetingAgent(window => ++calls == 1
            ? throw new InvalidOperationException("the model crashed")
            : [Insight(InsightKind.Point, "Tolerance", window.Finals[0].Id)]);
        await using var runner = new MeetingAgentRunner(room, agent, proposed.Enqueue, "de", batch: 1, quiet: Never);

        Say(room, "u1");
        await Until(() => calls == 1);
        Say(room, "u2");

        await Until(() => !proposed.IsEmpty);
        Assert.Equal(["u2"], Assert.Single(proposed).SourceUtteranceIds);
        Assert.True(room.Contains("u2"));
    }

    [Fact]
    public async Task TheNextWindowKnowsTheTopicsAlreadyFound()
    {
        var room = Room();
        var agent = new FakeMeetingAgent(window => [Insight(InsightKind.Point, "Delivery", window.Finals[0].Id)]);
        await using var runner = new MeetingAgentRunner(room, agent, _ => { }, "de", batch: 1, quiet: Never);

        Say(room, "u1");
        await Until(() => agent.Windows.Count == 1);
        Say(room, "u2");
        await Until(() => agent.Windows.Count == 2);

        Assert.Empty(agent.Windows.First().KnownTopics);
        Assert.Equal(["Delivery"], agent.Windows.Last().KnownTopics);
    }

    [Fact]
    public async Task StoppingCancelsTheCallInFlightAndNothingArrivesAfterwards()
    {
        var room = Room();
        var proposed = new ConcurrentQueue<MeetingInsight>();
        var cancelled = false;
        var agent = new FakeMeetingAgent(async (window, ct) =>
        {
            try
            {
                await Task.Delay(Timeout.Infinite, ct);
            }
            catch (OperationCanceledException)
            {
                cancelled = true;
            }

            return [Insight(InsightKind.Point, "Late", window.Finals[0].Id)];
        });
        var runner = new MeetingAgentRunner(room, agent, proposed.Enqueue, "de", batch: 1, quiet: Never);

        Say(room, "u1");
        await Until(() => !agent.Windows.IsEmpty);
        await runner.DisposeAsync();
        Say(room, "u2");

        Assert.True(cancelled);
        Assert.Empty(proposed);
        Assert.Single(agent.Windows);
    }
}
