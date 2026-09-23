namespace Kanal.Core.Workspaces;

internal sealed record StoredRun(
    string? Transcript, string? Audio, DateTimeOffset? StartedAt, DateTimeOffset? EndedAt)
{
    internal bool Complete =>
        Transcript is not null && StorePaths.IsFileName(Transcript) && StorePaths.IsFileName(Audio);

    // A record written before runs existed holds its one run under the two fixed names.
    internal static List<StoredRun> Legacy(
        string? transcript, string? audio, DateTimeOffset? startedAt, DateTimeOffset? endedAt) =>
        transcript is null && audio is null
            ? []
            : [new(transcript ?? TranscriptLog.FileName, audio, startedAt, endedAt)];

    // Case-blind on every system: two runs naming one file would overwrite each other.
    internal static bool AllComplete(IReadOnlyList<StoredRun?> runs)
    {
        if (runs.Any(r => r is null || !r.Complete))
            return false;

        var names = runs.SelectMany(r => new[] { r!.Transcript, r.Audio }).OfType<string>().ToList();
        return names.Distinct(StringComparer.OrdinalIgnoreCase).Count() == names.Count;
    }

    internal static StoredRun From(MeetingRun r, string folder) =>
        new(Path.GetRelativePath(folder, r.TranscriptPath),
            r.AudioPath is null ? null : Path.GetRelativePath(folder, r.AudioPath), r.StartedAt, r.EndedAt);

    internal MeetingRun In(string folder) =>
        new(Path.Combine(folder, Transcript!), Audio is null ? null : Path.Combine(folder, Audio),
            StartedAt, EndedAt);
}
