namespace Kanal.Audio;

public enum SystemAudioBackend
{
    Unavailable,
    WasapiLoopback,
    CoreAudioProcessTap,
    ScreenCaptureKit,
}

public enum SystemAudioPlatform
{
    Other,
    Windows,
    MacOS,
}

public sealed record SystemAudioSupport(
    bool IsAvailable,
    SystemAudioBackend Backend,
    string? Reason = null);

public interface ISystemAudioCaptureService
{
    SystemAudioBackend Backend { get; }

    IAsyncEnumerable<ReadOnlyMemory<byte>> CaptureAsync(CancellationToken ct);
}
