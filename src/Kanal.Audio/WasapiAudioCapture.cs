using System.Runtime.CompilerServices;
using System.Runtime.Versioning;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace Kanal.Audio;

/// <summary>
/// Windows capture via WASAPI shared mode. Accepts whatever mix format the device
/// delivers (typically float32 stereo at 44.1/48 kHz) and converts to 16 kHz mono PCM16.
/// The macOS counterpart is the open D0-A item and lives behind the same interface.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WasapiAudioCapture : IAudioCaptureService
{
    public const int TargetRateHz = AudioCaptureFormat.SampleRateHz;

    public IReadOnlyList<AudioDeviceInfo> GetDevices()
    {
        using var enumerator = new MMDeviceEnumerator();
        return enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active)
            .Select(d => new AudioDeviceInfo(d.ID, d.FriendlyName))
            .ToList();
    }

    public async IAsyncEnumerable<ReadOnlyMemory<byte>> CaptureAsync(
        string? deviceId, [EnumeratorCancellation] CancellationToken ct)
    {
        using var enumerator = new MMDeviceEnumerator();
        using var device = deviceId is null
            ? enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications)
            : enumerator.GetDevice(deviceId);
        using var capture = new WasapiCapture(device);

        await foreach (var frame in WasapiPcmCapture.RunAsync(capture, ct))
            yield return frame;
    }
}
