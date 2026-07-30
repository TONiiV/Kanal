namespace Kanal.Core.Providers;

public enum LatencyClass
{
    Realtime,
    Near,
    Batch,
}

/// <summary>
/// Capability declaration — the only thing the orchestrator inspects.
/// Translation == false means finals are routed through an <see cref="IMtProvider"/>.
/// </summary>
public sealed record AsrCapabilities(
    bool Streaming,
    bool Diarization,
    bool Translation,
    bool AutoLanguageDetect,
    IReadOnlySet<string> Languages,
    LatencyClass Latency);

public sealed record AsrSessionOptions(
    int SampleRateHz,
    IReadOnlyList<string> ExpectedLanguages,
    IReadOnlyList<string> TargetLanguages);

public interface IAsrProvider
{
    string Id { get; }
    AsrCapabilities Caps { get; }
    Task<IAsrSession> StartAsync(AsrSessionOptions options, CancellationToken ct);
}

public interface IAsrSession : IAsyncDisposable
{
    /// <summary>Push 16 kHz mono PCM16 little-endian audio.</summary>
    ValueTask PushAudioAsync(ReadOnlyMemory<byte> pcm16, CancellationToken ct = default);

    /// <summary>Normalized event stream. Completes when the session ends.</summary>
    IAsyncEnumerable<AsrEvent> Events { get; }
}

public abstract record AsrEvent
{
    public sealed record Transcript(
        string UtteranceId,
        string SpeakerTag,
        string Text,
        string SrcLang,
        long TStartMs,
        long? TEndMs,
        bool IsFinal,
        bool CodeSwitch,
        double SpeakerConfidence,
        IReadOnlyDictionary<string, string>? Translations) : AsrEvent;

    public sealed record Error(string Message, bool Fatal) : AsrEvent;

    public sealed record Ended(string? Reason) : AsrEvent;
}
