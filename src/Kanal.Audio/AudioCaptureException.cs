using System.Text.Json;

namespace Kanal.Audio;

public enum AudioCaptureFault
{
    SourceFailed,
    SourceEnded,
    PermissionDenied,
    DeviceUnavailable,
    ClockStalled,
    ConsumerStalled,
}

public sealed class AudioCaptureException(
    AudioCaptureFault fault, string message, string? source = null, Exception? inner = null)
    : IOException(message, inner)
{
    private const int AccessDenied = unchecked((int)0x80070005);
    private const int ElementNotFound = unchecked((int)0x80070490);
    private const int DeviceInvalidated = unchecked((int)0x88890004);
    private const int DeviceInUse = unchecked((int)0x8889000A);

    public AudioCaptureFault Fault { get; } = fault;

    public string Code => CodeOf(Fault);

    public string? SourceName { get; } = source;

    public static string CodeOf(AudioCaptureFault fault) =>
        JsonNamingPolicy.SnakeCaseLower.ConvertName(fault.ToString());

    public static AudioCaptureException Classify(Exception error, string source) => error switch
    {
        AudioCaptureException { SourceName: not null } known => known,
        AudioCaptureException known => new(known.Fault, known.Message, source, known.InnerException),
        UnauthorizedAccessException => new(AudioCaptureFault.PermissionDenied, error.Message, source, error),
        _ when error.HResult == AccessDenied => new(AudioCaptureFault.PermissionDenied, error.Message, source, error),
        _ when error.HResult is ElementNotFound or DeviceInvalidated or DeviceInUse =>
            new(AudioCaptureFault.DeviceUnavailable, error.Message, source, error),
        _ => new(AudioCaptureFault.SourceFailed, error.Message, source, error),
    };
}
