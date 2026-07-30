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

public sealed record RoomSnapshot(
    RoomConfig Config,
    IReadOnlyList<Speaker> Speakers,
    IReadOnlyList<Utterance> Utterances);

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
