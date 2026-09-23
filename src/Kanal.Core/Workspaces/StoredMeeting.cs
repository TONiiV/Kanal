using System.Text.Json.Serialization;

namespace Kanal.Core.Workspaces;

internal sealed record StoredMeeting(
    int SchemaVersion,
    string? Id,
    string? Title,
    DateTimeOffset CreatedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? EndedAt,
    List<string>? Languages,
    List<StoredRun>? Runs,
    // Schema 1 only: read as one run, never written, so the runs stay the single list of files.
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? TranscriptFileName,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? AudioFileName) : IStoredRecord
{
    [JsonIgnore]
    public bool Complete =>
        StorePaths.IsFolderName(Id)
        && !string.IsNullOrWhiteSpace(Title)
        && CreatedAt != default
        && Languages is not null
        && Languages.All(language => !string.IsNullOrWhiteSpace(language))
        && StoredRun.AllComplete(AllRuns);

    private List<StoredRun> AllRuns =>
        Runs ?? StoredRun.Legacy(TranscriptFileName, AudioFileName, StartedAt, EndedAt);

    internal static StoredMeeting From(MeetingRecord m, string folder) =>
        new(RecordFile.MeetingSchemaVersion, m.Id, m.Title, m.CreatedAt, m.StartedAt, m.EndedAt,
            [.. m.Languages], [.. m.Runs.Select(r => StoredRun.From(r, folder))], null, null);

    internal MeetingRecord ToRecord(string workspaceId, string folder) =>
        new(Id!, workspaceId, Title!, CreatedAt, StartedAt, EndedAt,
            Languages!, [.. AllRuns.Select(r => r.In(folder))]);
}
