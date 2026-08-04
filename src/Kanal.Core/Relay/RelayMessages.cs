using System.Text.Json;
using System.Text.Json.Serialization;
using Kanal.Core.Models;

namespace Kanal.Core.Relay;

/// <summary>
/// Wire messages between host and read-only clients. The host is the single
/// authority; clients are projections and can rebuild from room.snapshot.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(UtteranceUpsert), "utterance.upsert")]
[JsonDerivedType(typeof(TranslationUpsert), "translation.upsert")]
[JsonDerivedType(typeof(SpeakerUpsert), "speaker.upsert")]
[JsonDerivedType(typeof(RoomSnapshotMessage), "room.snapshot")]
[JsonDerivedType(typeof(RoomConfigMessage), "room.config")]
[JsonDerivedType(typeof(RoomClosedMessage), "room.closed")]
[JsonDerivedType(typeof(RoomMovedMessage), "room.moved")]
[JsonDerivedType(typeof(RoomPausedMessage), "room.paused")]
[JsonDerivedType(typeof(RoomRecordingMessage), "room.recording")]
[JsonDerivedType(typeof(SignedRelayMessage), "relay.signed")]
public abstract record RelayMessage;

/// <summary>Partial and final share one message; clients replace in place by Utterance.Id.</summary>
public sealed record UtteranceUpsert(Utterance Utterance) : RelayMessage;

/// <summary>Clients drop this if SourceRevision is behind their current revision.</summary>
public sealed record TranslationUpsert(
    string UtteranceId,
    int SourceRevision,
    IReadOnlyDictionary<string, string> Translations) : RelayMessage;

/// <summary>Rename and merge share one message; clients re-resolve all history bubbles.</summary>
public sealed record SpeakerUpsert(Speaker Speaker) : RelayMessage;

public sealed record RoomSnapshotMessage(RoomSnapshot Snapshot) : RelayMessage;

public sealed record RoomConfigMessage(RoomConfig Config) : RelayMessage;

/// <summary>
/// The meeting on this channel is over. Clients keep the transcript readable but stop
/// presenting themselves as live — a phone left on a dead channel otherwise looks connected.
/// </summary>
public sealed record RoomClosedMessage : RelayMessage;

/// <summary>
/// The operator restarted: a new room id (and channel) has taken over. Published on the
/// OLD channel so phones already holding a QR-scanned URL follow along without rescanning.
/// A fresh room id per Start is deliberate — ASR utterance ids restart at zero, so reusing
/// the channel would let a new meeting overwrite the previous one's records by id.
/// </summary>
public sealed record RoomMovedMessage(string NewRoomId, string NewVerificationKey) : RelayMessage;

/// <summary>
/// The room is temporarily off the record, or back on it. A column that simply stops is
/// indistinguishable from a broken connection, so the pause is stated rather than left to be
/// inferred — the same reasoning as <see cref="RoomClosedMessage"/>. Everything already said
/// stays readable; the meeting, the room id and the join URL are all unchanged.
/// </summary>
public sealed record RoomPausedMessage(bool Paused) : RelayMessage;

/// <summary>
/// Whether the host is writing the room's audio to a file. Sent to the clients because the
/// people being recorded are the ones entitled to know: the host's own status bar is read by
/// the operator alone, and in Germany and Poland recording a private conversation without the
/// other side knowing is not merely rude. Like pause, a state rather than an event, so it is
/// carried in the snapshot too.
/// </summary>
public sealed record RoomRecordingMessage(bool Recording) : RelayMessage;

/// <summary>
/// An exact relay JSON payload authenticated by the room's ephemeral P-256 key. The public
/// verification key is a bearer-link parameter; only the host holds the private key. Keeping
/// the serialized data intact avoids relying on JSON property ordering across C# and browsers.
/// </summary>
public sealed record SignedRelayMessage(int Version, string Data, string Signature) : RelayMessage;

/// <param name="Paused">
/// Carried here as well as in <see cref="RoomPausedMessage"/> because a phone joining mid-pause
/// never saw the announcement, and late join is served entirely from the snapshot.
/// </param>
public sealed record RoomSnapshot(
    RoomConfig Config,
    IReadOnlyList<Speaker> Speakers,
    IReadOnlyList<Utterance> Utterances,
    bool Paused = false,
    bool Recording = false);

public static class RelayJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public static string Serialize(RelayMessage message) =>
        JsonSerializer.Serialize(message, Options);

    public static RelayMessage? Deserialize(string json) =>
        JsonSerializer.Deserialize<RelayMessage>(json, Options);
}
