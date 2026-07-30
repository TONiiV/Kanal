namespace Kanal.Audio;

public sealed record AudioDeviceInfo(string Id, string Name);

/// <summary>
/// Produces 16 kHz mono PCM16 frames, whatever the physical device delivers.
/// Implementations own device format conversion and resampling.
/// </summary>
public interface IAudioCaptureService
{
    IReadOnlyList<AudioDeviceInfo> GetDevices();

    /// <summary>
    /// Capture from the given device (null = default). Yields PCM16 frames until cancelled.
    /// </summary>
    IAsyncEnumerable<ReadOnlyMemory<byte>> CaptureAsync(string? deviceId, CancellationToken ct);
}
