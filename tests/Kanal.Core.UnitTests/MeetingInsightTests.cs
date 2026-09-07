using Kanal.Core.Insights;
using Kanal.Core.Models;
using Kanal.Core.Providers;
using Kanal.Core.Room;

namespace Kanal.Core.UnitTests;

public class MeetingInsightTests
{
    private static RoomState RoomWith(params string[] utteranceIds)
    {
        var room = new RoomState(new RoomConfig("room", ["zh", "de"]));
        foreach (var id in utteranceIds)
            room.ApplyTranscript(new AsrEvent.Transcript(
                id, "S01", $"said {id}", "zh", 0, 1000, IsFinal: true, CodeSwitch: false, 0.9, null));
        return room;
    }

    private static MeetingInsight Insight(
        string id, InsightKind kind, string topic, params string[] sources) =>
        new(id, kind, topic, $"text of {id}", sources);

    [Fact]
    public void AnItemThatLeadsBackToNothingSaidIsNotRecorded()
    {
        var room = RoomWith("u1");
        var insights = new MeetingInsights(room);

        Assert.Null(insights.Record(Insight("i1", InsightKind.Decision, "Delivery", "u404")));
        Assert.Empty(insights.Items);
    }

    [Fact]
    public void ASourceTheRoomNeverHeardIsDroppedAndTheRestSurvive()
    {
        var room = RoomWith("u1", "u2");
        var insights = new MeetingInsights(room);

        var recorded = insights.Record(Insight("i1", InsightKind.Point, "Tolerance", "u1", "u404", "u2"));

        Assert.NotNull(recorded);
        Assert.Equal(["u1", "u2"], recorded.SourceUtteranceIds);
    }

    [Fact]
    public void ADecisionArrivesAsACandidateAndOnlyAPersonMakesItAcommitment()
    {
        var room = RoomWith("u1");
        var insights = new MeetingInsights(room);
        insights.Record(Insight("i1", InsightKind.Decision, "Delivery", "u1"));

        Assert.Equal(InsightState.Candidate, insights.Items.Single().State);

        Assert.True(insights.Confirm("i1"));
        Assert.Equal(InsightState.Confirmed, insights.Items.Single().State);
    }

    [Fact]
    public void ADismissedItemIsNeitherCandidateNorConfirmedAndCannotBeConfirmedLater()
    {
        var room = RoomWith("u1");
        var insights = new MeetingInsights(room);
        insights.Record(Insight("i1", InsightKind.Proposal, "Price", "u1"));

        Assert.True(insights.Dismiss("i1"));
        Assert.Equal(InsightState.Dismissed, insights.Items.Single().State);
        Assert.False(insights.Confirm("i1"));
        Assert.Equal(InsightState.Dismissed, insights.Items.Single().State);
    }

    [Fact]
    public void ATopicReadsPointThenProposalThenDecisionWhateverOrderTheyArrivedIn()
    {
        var room = RoomWith("u1", "u2", "u3");
        var insights = new MeetingInsights(room);
        insights.Record(Insight("i3", InsightKind.Decision, "Delivery", "u3"));
        insights.Record(Insight("i1", InsightKind.Point, "Delivery", "u1"));
        insights.Record(Insight("i2", InsightKind.Proposal, "Delivery", "u2"));
        insights.Record(Insight("i4", InsightKind.Point, "Tolerance", "u1"));

        var topics = insights.ByTopic();

        Assert.Equal(["Delivery", "Tolerance"], topics.Select(topic => topic.Topic));
        Assert.Equal(
            [InsightKind.Point, InsightKind.Proposal, InsightKind.Decision],
            topics[0].Items.Select(item => item.Kind));
    }

    [Fact]
    public void ConfirmingSomethingTheModelNeverProposedChangesNothing()
    {
        var insights = new MeetingInsights(RoomWith("u1"));

        Assert.False(insights.Confirm("nothing"));
        Assert.False(insights.Dismiss("nothing"));
    }
}
