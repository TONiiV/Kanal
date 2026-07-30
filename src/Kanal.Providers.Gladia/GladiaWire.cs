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
                    yield return new AsrEvent.Error(
                        GetStringAt(root, "data", "message") ?? GetString(root, "message") ?? json,
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
        return transcript;
    }

    private AsrEvent.Transcript? ParseTranslation(JsonElement root)
    {
        if (!root.TryGetProperty("data", out var data))
            return null;

        var id = GetString(data, "id")
                 ?? GetStringAt(data, "utterance", "id")
                 ?? GetStringAt(data, "original_utterance", "id");
        var targetLang = GetString(data, "target_language")
                         ?? GetStringAt(data, "translated_utterance", "language");
        var translated = GetStringAt(data, "translated_utterance", "text")
                         ?? GetString(data, "translation")
                         ?? GetString(data, "text");

        if (id is null || targetLang is null || translated is null)
            return null;
        if (!_transcripts.TryGetValue(id, out var known))
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
