using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Kanal.Core.Diagnostics;
using Kanal.Core.Models;

namespace Kanal.Core.Workspaces;

public static class TranscriptLog
{
    public const string FileName = "transcript.jsonl";

    internal const string LogCategory = "transcript";

    internal static readonly JsonSerializerOptions Options =
        new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    public static IReadOnlyList<Utterance> Read(string path)
    {
        if (!File.Exists(path))
            return [];

        var latest = new Dictionary<string, Utterance>();
        var order = new List<string>();
        try
        {
            // ReadWrite, not Read: the writer holds the file for the whole meeting, and Read
            // would be refused sharing against it — reading mid-meeting is the point of this.
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            while (reader.ReadLine() is { } line)
            {
                if (Parse(line) is not { } utterance)
                    continue;
                if (!latest.ContainsKey(utterance.Id))
                    order.Add(utterance.Id);
                latest[utterance.Id] = utterance;
            }
        }
        catch (Exception ex)
        {
            Log.Warning(LogCategory, $"{path} could not be read to the end.", ex);
        }

        return [.. order.Select(id => latest[id])];
    }

    // Null, never a throw: a host killed mid-append leaves its last line stopped in the middle
    // of itself, and every completed line above it is still a sentence somebody said.
    private static Utterance? Parse(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
            return null;

        try
        {
            var utterance = JsonSerializer.Deserialize<Utterance>(line, Options);
            return utterance is null
                   || string.IsNullOrEmpty(utterance.Id)
                   || utterance.SpeakerTag is null
                   || utterance.SrcLang is null
                   || utterance.SrcText is null
                ? null
                : utterance with { Translations = utterance.Translations ?? new Dictionary<string, string>() };
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <summary>
/// Fed from the room's own event, so <see cref="Append"/> must never throw: a full disk costs
/// the transcript, once, reported once, and never the meeting.
/// </summary>
public sealed class TranscriptLogWriter : IDisposable
{
    private readonly TextWriter _writer;
    private readonly Action<string> _onStopped;
    private readonly object _gate = new();
    private bool _stopped;

    public TranscriptLogWriter(string path, Action<string> onStopped)
        : this(Create(path), path, onStopped)
    {
    }

    /// <summary>Test seam: the failure policy has to be provable without filling a disk.</summary>
    public TranscriptLogWriter(TextWriter writer, string path, Action<string> onStopped)
    {
        _writer = writer;
        Path = path;
        _onStopped = onStopped;
    }

    public string Path { get; }

    public void Append(Utterance utterance)
    {
        lock (_gate)
        {
            if (_stopped)
                return;

            try
            {
                _writer.WriteLine(JsonSerializer.Serialize(utterance, TranscriptLog.Options));
                _writer.Flush();
            }
            catch (Exception ex)
            {
                _stopped = true;
                try
                {
                    _writer.Dispose();
                }
                catch
                {
                    // the same dead disk the write just met; the lines that reached it are kept
                }

                Log.Warning(TranscriptLog.LogCategory, $"{Path} stopped taking lines.", ex);
                _onStopped(ex.Message);
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_stopped)
                return;
            _stopped = true;
            _writer.Dispose();
        }
    }

    private static TextWriter Create(string path)
    {
        var folder = System.IO.Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(folder))
            Directory.CreateDirectory(folder);

        // FileShare.Read so the transcript can be read while the meeting is still running
        return new StreamWriter(
            new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }
}
