using Kanal.Core.Models;
using Kanal.Core.Providers;
using Kanal.Core.Relay;

namespace Kanal.Core.Room;

/// <summary>
/// The host's authoritative room state. Everything clients see is a projection
/// of this; reconnect and late join are served from <see cref="Snapshot"/>.
/// </summary>
public sealed class RoomState
{
    private static readonly string[] Palette =
    [
        "#B23A2E", "#1C6B58", "#2B57A0", "#9A6B10",
        "#6B4FA0", "#A03A6E", "#4A7A2E", "#8A5A3A",
    ];

    private readonly object _gate = new();
    private readonly Dictionary<string, Utterance> _utterances = new();
    private readonly List<string> _order = new();
    private readonly Dictionary<string, Speaker> _speakers = new();
    private readonly Dictionary<string, string> _tagAliases = new(); // merged tag → canonical tag

    public RoomState(RoomConfig config) => Config = config;

    public RoomConfig Config { get; private set; }

    public event Action<Utterance>? UtteranceUpserted;
    public event Action<Speaker>? SpeakerUpserted;
    public event Action<RoomConfig>? ConfigChanged;

    public Utterance ApplyTranscript(AsrEvent.Transcript t)
    {
        Utterance updated;
        Speaker? newSpeaker = null;
        lock (_gate)
        {
            var tag = ResolveTag(t.SpeakerTag);
            if (!_speakers.ContainsKey(tag))
            {
                newSpeaker = new Speaker(tag, null, [], Palette[_speakers.Count % Palette.Length]);
                _speakers[tag] = newSpeaker;
            }

            if (_utterances.TryGetValue(t.UtteranceId, out var existing))
            {
                var translations = MergeTranslations(existing.Translations, t.Translations);
                updated = existing with
                {
                    SpeakerTag = tag,
                    TStartMs = t.TStartMs,
                    TEndMs = t.TEndMs,
                    SrcLang = t.SrcLang,
                    SrcText = t.Text,
                    Revision = existing.Revision + 1,
                    State = t.IsFinal ? UtteranceState.Final : UtteranceState.Partial,
                    CodeSwitch = t.CodeSwitch,
                    SpeakerConfidence = t.SpeakerConfidence,
                    Translations = translations,
                };
            }
            else
            {
                updated = new Utterance(
                    t.UtteranceId, tag, t.TStartMs, t.TEndMs, t.SrcLang, t.Text,
                    Revision: 1,
                    t.IsFinal ? UtteranceState.Final : UtteranceState.Partial,
                    t.CodeSwitch, t.SpeakerConfidence,
                    MergeTranslations(null, t.Translations));
                _order.Add(t.UtteranceId);
            }

            _utterances[t.UtteranceId] = updated;
        }

        if (newSpeaker is not null)
            SpeakerUpserted?.Invoke(newSpeaker);
        UtteranceUpserted?.Invoke(updated);
        return updated;
    }

    /// <summary>
    /// Merge translations produced for a given source revision.
    /// Returns null (drop) when the utterance advanced past that revision.
    /// </summary>
    public Utterance? ApplyTranslations(
        string utteranceId, int sourceRevision, IReadOnlyDictionary<string, string> translations)
    {
        Utterance updated;
        lock (_gate)
        {
            if (!_utterances.TryGetValue(utteranceId, out var existing))
                return null;
            if (sourceRevision < existing.Revision)
                return null; // stale: source text changed after translation was requested

            updated = existing with { Translations = MergeTranslations(existing.Translations, translations) };
            _utterances[utteranceId] = updated;
        }

        UtteranceUpserted?.Invoke(updated);
        return updated;
    }

    public Speaker RenameSpeaker(string tag, string? displayName)
    {
        Speaker updated;
        lock (_gate)
        {
            var canonical = ResolveTag(tag);
            var existing = _speakers.TryGetValue(canonical, out var s)
                ? s
                : new Speaker(canonical, null, [], Palette[_speakers.Count % Palette.Length]);
            updated = existing with { DisplayName = displayName };
            _speakers[canonical] = updated;
        }

        SpeakerUpserted?.Invoke(updated);
        return updated;
    }

    /// <summary>
    /// Merge <paramref name="fromTag"/> into <paramref name="intoTag"/>. Non-destructive:
    /// utterances keep their original tag; clients resolve via MergedFrom.
    /// Future ASR events carrying the merged tag map to the canonical speaker.
    /// </summary>
    public Speaker MergeSpeakers(string fromTag, string intoTag)
    {
        Speaker updated;
        lock (_gate)
        {
            var from = ResolveTag(fromTag);
            var into = ResolveTag(intoTag);
            if (from == into)
                return _speakers[into];

            var target = _speakers.TryGetValue(into, out var s)
                ? s
                : new Speaker(into, null, [], Palette[_speakers.Count % Palette.Length]);

            var mergedFrom = new List<string>(target.MergedFrom);
            if (_speakers.TryGetValue(from, out var source))
            {
                mergedFrom.AddRange(source.MergedFrom.Where(t => !mergedFrom.Contains(t)));
                _speakers.Remove(from);
            }
            if (!mergedFrom.Contains(from))
                mergedFrom.Add(from);

            updated = target with { MergedFrom = mergedFrom };
            _speakers[into] = updated;

            _tagAliases[from] = into;
            // re-point aliases that resolved through `from`
            foreach (var key in _tagAliases.Where(kv => kv.Value == from).Select(kv => kv.Key).ToList())
                _tagAliases[key] = into;
        }

        SpeakerUpserted?.Invoke(updated);
        return updated;
    }

    /// <summary>Whether an utterance has already entered the record — i.e. it began on it.</summary>
    public bool Contains(string utteranceId)
    {
        lock (_gate)
        {
            return _utterances.ContainsKey(utteranceId);
        }
    }

    /// <summary>Resolve a raw diarization tag to its canonical (post-merge) speaker tag.</summary>
    public string ResolveTag(string tag)
    {
        lock (_gate)
        {
            return _tagAliases.TryGetValue(tag, out var canonical) ? canonical : tag;
        }
    }

    public IReadOnlyList<Utterance> RecentFinals(int count, string? excludeId = null)
    {
        lock (_gate)
        {
            var result = new List<Utterance>(count);
            for (var i = _order.Count - 1; i >= 0 && result.Count < count; i--)
            {
                var u = _utterances[_order[i]];
                if (u.State == UtteranceState.Final && u.Id != excludeId)
                    result.Add(u);
            }

            result.Reverse();
            return result;
        }
    }

    public RoomSnapshot Snapshot()
    {
        lock (_gate)
        {
            return new RoomSnapshot(
                Config,
                _speakers.Values.ToList(),
                _order.Select(id => _utterances[id]).ToList());
        }
    }

    public void SetConfig(RoomConfig config)
    {
        lock (_gate)
        {
            Config = config;
        }

        ConfigChanged?.Invoke(config);
    }

    private static IReadOnlyDictionary<string, string> MergeTranslations(
        IReadOnlyDictionary<string, string>? existing, IReadOnlyDictionary<string, string>? incoming)
    {
        if (incoming is null || incoming.Count == 0)
            return existing ?? new Dictionary<string, string>();

        var merged = existing is null
            ? new Dictionary<string, string>()
            : new Dictionary<string, string>(existing);
        foreach (var (lang, text) in incoming)
            merged[lang] = text;
        return merged;
    }
}
