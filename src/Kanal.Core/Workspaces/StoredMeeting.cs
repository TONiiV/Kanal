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
    string? TranscriptFileName,
    string? AudioFileName) : IStoredRecord
{
    [JsonIgnore]
    public bool Complete =>
        StorePaths.IsFolderName(Id)
        && !string.IsNullOrWhiteSpace(Title)
        && CreatedAt != default
        && Languages is not null
        && Languages.All(language => !string.IsNullOrWhiteSpace(language))
        && StorePaths.IsFileName(TranscriptFileName)
        && StorePaths.IsFileName(AudioFileName);

    internal static StoredMeeting From(MeetingRecord m, string folder) =>
        new(RecordFile.SchemaVersion, m.Id, m.Title, m.CreatedAt, m.StartedAt, m.EndedAt,
            [.. m.Languages], FileName(m.TranscriptPath, folder), FileName(m.AudioPath, folder));

    internal MeetingRecord ToRecord(string workspaceId, string folder) =>
        new(Id!, workspaceId, Title!, CreatedAt, StartedAt, EndedAt,
            Languages!, ArtifactPath(folder, TranscriptFileName), ArtifactPath(folder, AudioFileName));

    private static string? FileName(string? path, string folder) =>
        path is null ? null : Path.GetRelativePath(folder, path);

    private static string? ArtifactPath(string folder, string? fileName) =>
        fileName is null ? null : Path.Combine(folder, fileName);
}
