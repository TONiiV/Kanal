using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Kanal.Core.Diagnostics;
using Kanal.Core.Models;

namespace Kanal.Core.Workspaces;

public sealed record MeetingManifest(
    string Id,
    string Title,
    IReadOnlyList<string> Languages,
    DateTimeOffset? StartedAt,
    DateTimeOffset? EndedAt);

/// <summary>One meeting, one file, plantable in any workspace (ADR 0054, decisions 22–23).</summary>
public static class MeetingBundle
{
    public const string Extension = ".kl";
    public const string ManifestFileName = "manifest.json";
    public const string TranscriptFileName = "transcript.md";
    private const string AttachmentsPrefix = "attachments/";
    private const string LogCategory = "workspaces";

    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    public static bool Holds(string path) =>
        string.Equals(Path.GetExtension(path), Extension, StringComparison.OrdinalIgnoreCase);

    public static StoreProblem? Write(
        WorkspaceStore store, MeetingRecord meeting, bool includeAudio, string target)
    {
        if (store.MeetingFolder(meeting.WorkspaceId, meeting.Id) is not { } folder)
            return StoreProblems.NoSuchMeeting(meeting.Id);

        try
        {
            using var file = new FileStream(target, FileMode.Create, FileAccess.Write);
            using var zip = new ZipArchive(file, ZipArchiveMode.Create);
            Put(zip, ManifestFileName, JsonSerializer.Serialize(
                StoredManifest.From(meeting, folder, includeAudio), Options));
            Put(zip, TranscriptFileName, Markdown(meeting, TranscriptLog.Read(meeting)));
            foreach (var run in meeting.Runs)
            {
                if (File.Exists(run.TranscriptPath))
                    zip.CreateEntryFromFile(run.TranscriptPath, Path.GetFileName(run.TranscriptPath));
                if (includeAudio && File.Exists(run.AudioPath))
                    zip.CreateEntryFromFile(run.AudioPath, Path.GetFileName(run.AudioPath));
            }

            var attachments = Path.Combine(folder, "attachments");
            if (Directory.Exists(attachments))
                foreach (var path in Directory.EnumerateFiles(attachments).Order())
                    zip.CreateEntryFromFile(path, AttachmentsPrefix + Path.GetFileName(path));

            return null;
        }
        catch (Exception ex)
        {
            Log.Error(LogCategory, $"The bundle could not be written to {target}.", ex);
            return StoreProblems.Unwritable(target, ex.Message);
        }
    }

    public static (MeetingManifest? Manifest, StoreProblem? Problem) ReadManifest(string bundlePath)
    {
        var (stored, problem) = ReadStoredManifest(bundlePath);
        return (stored?.ToManifest(), problem);
    }

    private static (StoredManifest? Manifest, StoreProblem? Problem) ReadStoredManifest(string bundlePath)
    {
        try
        {
            using var zip = ZipFile.OpenRead(bundlePath);
            if (zip.GetEntry(ManifestFileName) is not { } entry)
                return (null, StoreProblems.Unreadable(bundlePath, "That file is not a Kanal meeting bundle."));

            using var reader = new StreamReader(entry.Open(), Encoding.UTF8);
            return RecordFile.Parse<StoredManifest>(reader.ReadToEnd(), bundlePath, RecordFile.MeetingSchemaVersion);
        }
        catch (Exception ex)
        {
            Log.Warning(LogCategory, $"{bundlePath} could not be read as a bundle.", ex);
            return (null, StoreProblems.Unreadable(bundlePath, ex.Message));
        }
    }

    /// <summary>
    /// Plants the bundle in <paramref name="workspaceId"/>. With <paramref name="asNewRecord"/>
    /// the content arrives under a fresh id beside whatever already holds the original one —
    /// there is no overwrite, here or anywhere above this (decision 23).
    /// </summary>
    public static MeetingResult Import(
        WorkspaceStore store, string workspaceId, string bundlePath, bool asNewRecord)
    {
        var (manifest, problem) = ReadStoredManifest(bundlePath);
        if (problem is not null)
            return new MeetingResult(null, problem);

        var runs = manifest!.AllRuns;
        var names = runs.SelectMany(r => new[] { r.Transcript, r.Audio }).OfType<string>().ToHashSet();

        MeetingRecord record;
        if (asNewRecord)
        {
            var (made, trouble) = store.CreateMeeting(
                workspaceId, FreeTitle(store, workspaceId, manifest.Title!));
            if (trouble is not null)
                return new MeetingResult(null, trouble);

            record = made! with
            {
                StartedAt = manifest.StartedAt,
                EndedAt = manifest.EndedAt,
                Languages = manifest.Languages!,
            };
        }
        else
        {
            record = new MeetingRecord(
                manifest.Id!, workspaceId, manifest.Title!,
                manifest.StartedAt ?? DateTimeOffset.UtcNow,
                manifest.StartedAt, manifest.EndedAt, manifest.Languages!, []);
        }

        if (store.MeetingFolder(workspaceId, record.Id) is not { } folder)
            return new MeetingResult(null, StoreProblems.NotAMeetingId(record.Id));

        try
        {
            using var zip = ZipFile.OpenRead(bundlePath);
            Directory.CreateDirectory(folder);
            foreach (var entry in zip.Entries.Where(e => names.Contains(e.FullName) || Wanted(e.FullName)))
            {
                var target = Path.Combine(folder, entry.FullName.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                entry.ExtractToFile(target, overwrite: true);
            }
        }
        catch (Exception ex)
        {
            Log.Error(LogCategory, $"{bundlePath} could not be unpacked into {folder}.", ex);
            return new MeetingResult(null, StoreProblems.Unwritable(folder, ex.Message));
        }

        return store.SaveMeeting(record with
        {
            Runs = [.. runs.Select(r => r.In(folder) with { AudioPath = Kept(folder, r.Audio) })],
        });
    }

    /// <summary>The name the save dialog offers, and the one the Markdown export offers too.</summary>
    public static string SuggestedFileName(string title)
    {
        var cleaned = new string([.. title.Select(
            c => NotInAFileName.Contains(c) || char.IsControl(c) ? '-' : c)]).Trim(' ', '.');
        return cleaned.Length == 0 ? "meeting" : cleaned;
    }

    // Windows' invalid set is a superset of the others', so one list serves all.
    private const string NotInAFileName = "/\\:*?\"<>|";

    // A zip entry is a string from another machine: "../" in one would land a file in the
    // workspace above the meeting, and an absolute one anywhere on the disk.
    private static bool Wanted(string name) =>
        name is TranscriptFileName
        || (name.StartsWith(AttachmentsPrefix, StringComparison.Ordinal)
            && PlainName(name[AttachmentsPrefix.Length..]));

    private static bool PlainName(string name) =>
        !string.IsNullOrWhiteSpace(name)
        && !name.Contains('/') && !name.Contains('\\')
        && name is not ("." or "..");

    private static string? Kept(string folder, string? name) =>
        name is not null && File.Exists(Path.Combine(folder, name)) ? Path.Combine(folder, name) : null;

    private static string FreeTitle(WorkspaceStore store, string workspaceId, string title)
    {
        var taken = store.ListMeetings(workspaceId).Meetings.Select(m => m.Title).ToHashSet();
        var free = title;
        for (var copy = 2; taken.Contains(free); copy++)
            free = $"{title} ({copy})";

        return free;
    }

    private static void Put(ZipArchive zip, string name, string text)
    {
        using var writer = new StreamWriter(
            zip.CreateEntry(name).Open(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        writer.Write(text);
    }

    // The speaker tag, not a name: the jsonl is the whole record of the meeting on disk, and it
    // carries the diarization tag only.
    private static string Markdown(MeetingRecord meeting, IReadOnlyList<Utterance> utterances)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# Kanal — {meeting.Title}");
        sb.AppendLine();
        sb.AppendLine($"started-at: {meeting.StartedAt:O}");
        sb.AppendLine($"ended-at: {meeting.EndedAt:O}");
        sb.AppendLine();
        foreach (var u in utterances.Where(u => u.State == UtteranceState.Final))
        {
            sb.AppendLine($"**{u.SpeakerTag}** ({u.SrcLang}): {u.SrcText}");
            foreach (var (lang, text) in u.Translations.OrderBy(t => t.Key))
                sb.AppendLine($"  - {lang}: {text}");
            sb.AppendLine();
        }

        return sb.ToString();
    }

    private sealed record StoredManifest(
        int SchemaVersion,
        string? Id,
        string? Title,
        List<string>? Languages,
        DateTimeOffset? StartedAt,
        DateTimeOffset? EndedAt,
        List<StoredRun>? Runs = null) : IStoredRecord
    {
        [JsonIgnore]
        public bool Complete =>
            StorePaths.IsFolderName(Id)
            && !string.IsNullOrWhiteSpace(Title)
            && Languages is not null
            && Languages.All(language => !string.IsNullOrWhiteSpace(language))
            && StoredRun.AllComplete(AllRuns);

        internal List<StoredRun> AllRuns =>
            Runs ?? StoredRun.Legacy(TranscriptLog.FileName, WorkspaceStore.AudioFileName, StartedAt, EndedAt);

        internal static StoredManifest From(MeetingRecord m, string folder, bool includeAudio) =>
            new(RecordFile.MeetingSchemaVersion, m.Id, m.Title, [.. m.Languages], m.StartedAt, m.EndedAt,
                [.. m.Runs.Select(r => StoredRun.From(includeAudio ? r : r with { AudioPath = null }, folder))]);

        internal MeetingManifest ToManifest() =>
            new(Id!, Title!, Languages!, StartedAt, EndedAt);
    }
}
