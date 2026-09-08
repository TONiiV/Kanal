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

/// <summary>Rejects a stale endpoint instead of quietly capturing a different one.</summary>
public interface ISystemAudioCaptureService : IAudioCaptureService
{
    SystemAudioBackend Backend { get; }
}
