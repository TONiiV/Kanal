using System.Globalization;
using System.Text.Json;
using Kanal.Core.Diagnostics;
using Kanal.Core.Models;
using Kanal.Core.Workspaces;

namespace Kanal.Core.Meetings;

public sealed record SpeakerTurn(double StartSeconds, double EndSeconds, int Speaker);

public interface ISpeakerDiarizer
{
    Task<IReadOnlyList<SpeakerTurn>> DiarizeAsync(
        string wavPath, int? speakerCount, IProgress<double>? progress, CancellationToken ct);
}

// ADR 0055 slice 3. transcript.jsonl is never rewritten: the result is a sidecar that maps an
// utterance id to its new tag, so deleting the sidecar restores the original tags.
public static class SpeakerReidentification
{
    public const string FileName = "speakers.json";

    private const string LogCategory = "speakers";

    public static async Task<int> RunAsync(
        ISpeakerDiarizer diarizer,
        string transcriptPath,
        string audioPath,
        int? speakerCount,
        IProgress<double>? progress,
        CancellationToken ct)
    {
        var utterances = TranscriptLog.Read(transcriptPath);
        var turns = await diarizer.DiarizeAsync(audioPath, speakerCount, progress, ct);
        var tags = Assign(utterances, turns);
        Write(Path.GetDirectoryName(transcriptPath)!, tags);
        return tags.Values.Distinct().Count();
    }

    // The ASR clock and the recording both start on the first accepted frame and both skip a
    // pause, so an utterance's milliseconds are its offset into the recording.
    public static IReadOnlyDictionary<string, string> Assign(
        IReadOnlyList<Utterance> utterances, IReadOnlyList<SpeakerTurn> turns)
    {
        var tagOf = new Dictionary<int, string>();
        var tags = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var utterance in utterances.OrderBy(u => u.TStartMs))
        {
            if (utterance.TEndMs is not { } endMs)
                continue;

            var start = utterance.TStartMs / 1000.0;
            var end = endMs / 1000.0;
            // ponytail: O(utterances × turns); an hour is ~1000 × ~2000, index the turns if that grows.
            var best = turns
                .GroupBy(t => t.Speaker)
                .Select(g => (Speaker: g.Key, Overlap: g.Sum(t =>
                    Math.Max(0, Math.Min(end, t.EndSeconds) - Math.Max(start, t.StartSeconds)))))
                .Where(s => s.Overlap > 0)
                .OrderByDescending(s => s.Overlap)
                .ThenBy(s => s.Speaker)
                .FirstOrDefault();
            if (best.Overlap <= 0)
                continue;

            if (!tagOf.TryGetValue(best.Speaker, out var tag))
            {
                tag = string.Create(CultureInfo.InvariantCulture, $"D{tagOf.Count + 1:00}");
                tagOf[best.Speaker] = tag;
            }

            tags[utterance.Id] = tag;
        }

        return tags;
    }

    public static IReadOnlyList<Utterance> Apply(
        IReadOnlyList<Utterance> utterances, IReadOnlyDictionary<string, string> tags) =>
        tags.Count == 0
            ? utterances
            : [.. utterances.Select(u => tags.TryGetValue(u.Id, out var tag) ? u with { SpeakerTag = tag } : u)];

    public static IReadOnlyDictionary<string, string> Read(string meetingFolder)
    {
        var path = Path.Combine(meetingFolder, FileName);
        if (!File.Exists(path))
            return new Dictionary<string, string>();

        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path))
                   ?? new Dictionary<string, string>();
        }
        catch (Exception ex)
        {
            Log.Warning(LogCategory, $"{path} could not be read; the original speaker tags are shown.", ex);
            return new Dictionary<string, string>();
        }
    }

    public static void Write(string meetingFolder, IReadOnlyDictionary<string, string> tags)
    {
        var path = Path.Combine(meetingFolder, FileName);
        var part = path + ".part";
        File.WriteAllText(part, JsonSerializer.Serialize(tags));
        File.Move(part, path, overwrite: true);
    }
}
