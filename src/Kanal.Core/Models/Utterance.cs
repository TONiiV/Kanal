namespace Kanal.Core.Models;

public enum UtteranceState
{
    Partial,
    Final,
}

/// <summary>
/// One spoken segment. SpeakerTag comes from blind diarization and may drift;
/// resolve display identity through <see cref="Speaker.MergedFrom"/>.
/// </summary>
public sealed record Utterance(
    string Id,
    string SpeakerTag,
    long TStartMs,
    long? TEndMs,
    string SrcLang,
    string SrcText,
    int Revision,
    UtteranceState State,
    bool CodeSwitch,
    double SpeakerConfidence,
    IReadOnlyDictionary<string, string> Translations);
