using Kanal.Core.Insights;
using Kanal.Core.Models;
using Kanal.Core.Providers;
using Kanal.Core.Room;

namespace Kanal.Core.UnitTests;

public class PinnedPointTests
{
    private static MeetingInsights InsightsWith(params string[] utteranceIds)
    {
        var room = new RoomState(new RoomConfig("room", ["zh", "de"]));
        foreach (var id in utteranceIds)
            room.ApplyTranscript(new AsrEvent.Transcript(
                id, "S01", $"said {id}", "zh", 0, 1000, IsFinal: true, CodeSwitch: false, 0.9, null));
        return new MeetingInsights(room);
    }

    [Fact]
    public void APinIsAConfirmedPointWithTheUtteranceAsItsSource()
    {
        var insights = InsightsWith("u1");

        var pin = insights.Pin("u1", "Das Bauteil passt nicht.");

        Assert.NotNull(pin);
        Assert.Equal(InsightKind.Point, pin.Kind);
        Assert.Equal(InsightState.Confirmed, pin.State);
        Assert.Equal(MeetingInsights.PinnedTopic, pin.Topic);
        Assert.Equal("Das Bauteil passt nicht.", pin.Text);
        Assert.Equal(["u1"], pin.SourceUtteranceIds);
        Assert.Single(insights.Items);
    }

    [Fact]
    public void AnUtteranceTheRoomNeverHeardCannotBePinned()
    {
        var insights = InsightsWith("u1");

        Assert.Null(insights.Pin("u404", "nothing"));
        Assert.Empty(insights.Items);
    }

    [Fact]
    public void ASecondPinOfTheSameUtteranceDoesNothing()
    {
        var insights = InsightsWith("u1");
        insights.Pin("u1", "first");

        Assert.Null(insights.Pin("u1", "second"));

        Assert.Equal(["first"], insights.Items.Select(i => i.Text));
    }

    [Fact]
    public void AnUnpinnedUtteranceCanBePinnedAgain()
    {
        var insights = InsightsWith("u1");
        var pin = insights.Pin("u1", "first")!;
        insights.Dismiss(pin.Id);

        var again = insights.Pin("u1", "again");

        Assert.NotNull(again);
        Assert.NotEqual(pin.Id, again.Id);
    }

    [Fact]
    public void AnAnalystCannotRecordIntoThePinnedTopic()
    {
        var insights = InsightsWith("u1");

        var recorded = insights.Record(
            new MeetingInsight("i1", InsightKind.Point, MeetingInsights.PinnedTopic, "analyst text", ["u1"]));

        Assert.Null(recorded);
        Assert.Empty(insights.Items);
        Assert.NotNull(insights.Pin("u1", "pinned"));
    }

    [Fact]
    public void AnAnalystPointOnTheSameUtteranceDoesNotBlockAPin()
    {
        var insights = InsightsWith("u1");
        insights.Record(new MeetingInsight("i1", InsightKind.Point, "Delivery", "analyst text", ["u1"]));

        Assert.NotNull(insights.Pin("u1", "pinned"));
        Assert.Equal(2, insights.Items.Count);
    }
}
