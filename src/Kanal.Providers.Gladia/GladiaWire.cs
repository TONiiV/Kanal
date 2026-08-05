using System.Globalization;
using System.Text.Json;
using Kanal.Core.Providers;

namespace Kanal.Providers.Gladia;

/// <summary>
/// Maps Gladia live v2 websocket messages onto normalized <see cref="AsrEvent"/>s.
/// Parsing is deliberately lenient (unknown message types and fields are ignored)
/// because this is the one file that has to survive contact with the real API at D0-B.
/// Stateful: transcripts are cached per utterance id so that later translation
/// messages can be re-emitted as full Transcript events carrying the new translation.
/// </summary>
internal sealed class GladiaWire
{
    private readonly Dictionary<string, AsrEvent.Transcript> _transcripts = new();
    // transcript ids look like "00_00000003" (channel_sequence); translation messages
    // reference the same utterance as utterance_id "3" + channel — map between the two
    private readonly Dictionary<(int Channel, long Seq), string> _idBySeq = new();

    public IEnumerable<AsrEvent> Parse(string json)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            yield break;
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("type", out var typeProp))
                yield break;

            switch (typeProp.GetString())
            {
                case "transcript":
                    if (ParseTranscript(root) is { } transcript)
                        yield return transcript;
                    break;

                case "translation":
                    if (ParseTranslation(root) is { } updated)
                        yield return updated;
                    break;

                case "error":
                    // The frame's own message, or a description of it — never the frame. An error
                    // frame carries back the request that caused it, and that request contains what
                    // was said in the room; this string is shown on screen and written to the log,
                    // so dropping the whole frame in here put utterances in both.
                    yield return new AsrEvent.Error(
                        GetStringAt(root, "data", "message")
                        ?? GetString(root, "message")
                        ?? DescribeError(root),
                        Fatal: false);
                    break;

                    // speech_start / speech_end / audio_chunk acks / lifecycle → ignored
            }
        }
    }

    private AsrEvent.Transcript? ParseTranscript(JsonElement root)
    {
        if (!root.TryGetProperty("data", out var data))
            return null;

        var utterance = data.TryGetProperty("utterance", out var u) ? u : data;
        var text = GetString(utterance, "text");
        if (text is null)
            return null;

        var id = GetString(data, "id") ?? GetString(utterance, "id") ?? Guid.NewGuid().ToString("N");
        var isFinal = (data.TryGetProperty("is_final", out var f) && f.ValueKind == JsonValueKind.True)
                      || (root.TryGetProperty("is_final", out var f2) && f2.ValueKind == JsonValueKind.True);
        var lang = GetString(utterance, "language") ?? "und";
        var startMs = GetSecondsAsMs(utterance, "start") ?? 0;
        var endMs = GetSecondsAsMs(utterance, "end");
        var speakerTag = GetSpeakerTag(utterance) ?? GetSpeakerTag(data) ?? "S01";
        var confidence = GetDouble(utterance, "confidence") ?? 1.0;

        var transcript = new AsrEvent.Transcript(
            id, speakerTag, text, lang, startMs, isFinal ? endMs : null, isFinal,
            CodeSwitch: false, confidence, Translations: null);
        _transcripts[id] = transcript;

        var parts = id.Split('_');
        if (parts.Length == 2 && int.TryParse(parts[0], out var ch) && long.TryParse(parts[1], out var seq))
            _idBySeq[(ch, seq)] = id;

        return transcript;
    }

    private AsrEvent.Transcript? ParseTranslation(JsonElement root)
    {
        if (!root.TryGetProperty("data", out var data))
            return null;

        var targetLang = GetString(data, "target_language")
                         ?? GetStringAt(data, "translated_utterance", "language");
        var translated = GetStringAt(data, "translated_utterance", "text")
                         ?? GetString(data, "translation")
                         ?? GetString(data, "text");
        if (targetLang is null || translated is null)
            return null;

        // Gladia "translates" the source language into itself with garbage output — drop
        var originalLang = GetString(data, "original_language") ?? GetStringAt(data, "utterance", "language");
        if (string.Equals(targetLang, originalLang, StringComparison.OrdinalIgnoreCase))
            return null;

        var id = GetString(data, "id")
                 ?? GetStringAt(data, "utterance", "id")
                 ?? GetStringAt(data, "original_utterance", "id");
        if (id is null &&
            GetString(data, "utterance_id") is { } seqText && long.TryParse(seqText, out var seq))
        {
            var channel = GetIntAt(data, "utterance", "channel") ?? 0;
            _idBySeq.TryGetValue((channel, seq), out id);
        }

        if (id is null || !_transcripts.TryGetValue(id, out var known))
            return null; // translation for an utterance we never saw — drop

        var translations = known.Translations is null
            ? new Dictionary<string, string>()
            : new Dictionary<string, string>(known.Translations);
        translations[targetLang] = translated;

        var updated = known with { Translations = translations };
        _transcripts[id] = updated;
        return updated;
    }

    private static string? GetSpeakerTag(JsonElement element)
    {
        if (!element.TryGetProperty("speaker", out var speaker))
            return null;
        return speaker.ValueKind switch
        {
            JsonValueKind.Number => $"S{speaker.GetInt32() + 1:D2}",
            JsonValueKind.String => speaker.GetString(),
            _ => null,
        };
    }

    /// <summary>
    /// What an error frame was, for a frame that carries no message of its own: its status code if
    /// there is one, and nothing else from it. Enough to look up, and free of anything anyone said.
    /// </summary>
    private static string DescribeError(JsonElement root) =>
        GetIntAt(root, "data", "code") is { } code
            ? $"the transcription service rejected a request ({code})"
            : "the transcription service reported an error with no message";

    private static string? GetString(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(name, out var prop) && prop.ValueKind == JsonValueKind.String
            ? prop.GetString()
            : null;

    private static string? GetStringAt(JsonElement element, string objectName, string name) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(objectName, out var nested)
            ? GetString(nested, name)
            : null;

    private static int? GetIntAt(JsonElement element, string objectName, string name) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(objectName, out var nested) &&
        nested.ValueKind == JsonValueKind.Object &&
        nested.TryGetProperty(name, out var prop) && prop.ValueKind == JsonValueKind.Number
            ? prop.GetInt32()
            : null;

    private static double? GetDouble(JsonElement element, string name) =>
        element.TryGetProperty(name, out var prop) && prop.ValueKind == JsonValueKind.Number
            ? prop.GetDouble()
            : null;

    private static long? GetSecondsAsMs(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var prop) || prop.ValueKind != JsonValueKind.Number)
            return null;
        return (long)Math.Round(prop.GetDouble() * 1000, MidpointRounding.AwayFromZero);
    }
}
