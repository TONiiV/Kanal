using Kanal.Core.Models;
using Kanal.Core.Relay;

namespace Kanal.Tests;

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
        var json = RelayJson.Serialize(new RoomMovedMessage("kanal-093005-x7kq"));
        var restored = RelayJson.Deserialize(json);

        Assert.Contains("\"type\":\"room.moved\"", json);
        Assert.Equal("kanal-093005-x7kq", Assert.IsType<RoomMovedMessage>(restored).NewRoomId);
    }

    [Fact]
    public void SnapshotRoundTrips()
    {
        var snapshot = new RoomSnapshot(
            new RoomConfig("room", ["zh", "pl"]),
            [new Speaker("S01", "王工", ["S03"], "#B23A2E")],
            []);

        var restored = RelayJson.Deserialize(RelayJson.Serialize(new RoomSnapshotMessage(snapshot)));

        var message = Assert.IsType<RoomSnapshotMessage>(restored);
        Assert.Equal("王工", message.Snapshot.Speakers[0].DisplayName);
        Assert.Equal(["S03"], message.Snapshot.Speakers[0].MergedFrom);
    }
}
