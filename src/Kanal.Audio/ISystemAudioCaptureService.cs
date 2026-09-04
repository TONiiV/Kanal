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

/// <summary>
/// A platform system-output source. Like microphone capture, every implementation emits
/// 16 kHz mono PCM16 and rejects a stale endpoint instead of choosing a different one.
/// </summary>
public interface ISystemAudioCaptureService : IAudioCaptureService
{
    SystemAudioBackend Backend { get; }
}
