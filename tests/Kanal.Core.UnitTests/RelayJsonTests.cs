using Kanal.Core.Models;
using Kanal.Core.Relay;

namespace Kanal.Core.UnitTests;

public class RelayJsonTests
{
    [Fact]
    public void UtteranceUpsertRoundTripsWithTypeDiscriminator()
    {
        var utterance = new Utterance(
            "u1", "S01", 0, 1200, "zh", "料号 KX-4402", 3, UtteranceState.Final,
            CodeSwitch: false, SpeakerConfidence: 0.92,
            new Dictionary<string, string> { ["de"] = "Teilenummer KX-4402" });

        var json = RelayJson.Serialize(new UtteranceUpsert(utterance));
        var restored = RelayJson.Deserialize(json);

        Assert.Contains("\"type\":\"utterance.upsert\"", json);
        var upsert = Assert.IsType<UtteranceUpsert>(restored);
        Assert.Equal(utterance.SrcText, upsert.Utterance.SrcText);
        Assert.Equal("Teilenummer KX-4402", upsert.Utterance.Translations["de"]);
    }

    [Fact]
    public void RoomClosedRoundTrips()
    {
        var json = RelayJson.Serialize(new RoomClosedMessage());

        Assert.Contains("\"type\":\"room.closed\"", json);
        Assert.IsType<RoomClosedMessage>(RelayJson.Deserialize(json));
    }

    [Fact]
    public void RoomMovedCarriesTheNewRoomId()
    {
        var json = RelayJson.Serialize(new RoomMovedMessage(
            "kanal-093005-capability", "new-public-key", "new-reader-ticket"));
        var restored = RelayJson.Deserialize(json);

        Assert.Contains("\"type\":\"room.moved\"", json);
        var moved = Assert.IsType<RoomMovedMessage>(restored);
        Assert.Equal("kanal-093005-capability", moved.NewRoomId);
        Assert.Equal("new-public-key", moved.NewVerificationKey);
        Assert.Equal("new-reader-ticket", moved.NewInviteTicket);
    }

    /// <summary>The client switches on the camelCase name, so the casing is part of the contract.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void RoomPausedCarriesWhichWay(bool paused)
    {
        var json = RelayJson.Serialize(new RoomPausedMessage(paused));
        var restored = RelayJson.Deserialize(json);

        Assert.Contains("\"type\":\"room.paused\"", json);
        Assert.Contains($"\"paused\":{paused.ToString().ToLowerInvariant()}", json);
        Assert.Equal(paused, Assert.IsType<RoomPausedMessage>(restored).Paused);
    }

    /// <summary>The client switches on the camelCase name, so the casing is part of the contract.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void RoomRecordingCarriesWhichWay(bool recording)
    {
        var json = RelayJson.Serialize(new RoomRecordingMessage(recording));
        var restored = RelayJson.Deserialize(json);

        Assert.Contains("\"type\":\"room.recording\"", json);
        Assert.Contains($"\"recording\":{recording.ToString().ToLowerInvariant()}", json);
        Assert.Equal(recording, Assert.IsType<RoomRecordingMessage>(restored).Recording);
    }

    [Fact]
    public void RoomTranscribingRoundTrips()
    {
        var json = RelayJson.Serialize(new RoomTranscribingMessage(true));

        Assert.Contains("\"type\":\"room.transcribing\"", json);
        Assert.True(Assert.IsType<RoomTranscribingMessage>(RelayJson.Deserialize(json)).Transcribing);
    }

    [Fact]
    public void SnapshotRoundTrips()
    {
        var snapshot = new RoomSnapshot(
            new RoomConfig("room", ["zh", "pl"]),
            [new Speaker("S01", "王工", ["S03"], "#B23A2E")],
            [], Transcribing: true);

        var restored = RelayJson.Deserialize(RelayJson.Serialize(new RoomSnapshotMessage(snapshot)));

        var message = Assert.IsType<RoomSnapshotMessage>(restored);
        Assert.Equal("王工", message.Snapshot.Speakers[0].DisplayName);
        Assert.Equal(["S03"], message.Snapshot.Speakers[0].MergedFrom);
        Assert.True(message.Snapshot.Transcribing);
    }
}
