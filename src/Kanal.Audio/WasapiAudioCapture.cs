using System.Runtime.CompilerServices;
using System.Runtime.Versioning;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace Kanal.Audio;

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
