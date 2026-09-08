using System.Runtime.CompilerServices;
using System.Runtime.Versioning;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace Kanal.Audio;

[SupportedOSPlatform("windows")]
public sealed class WasapiLoopbackAudioCapture : ISystemAudioCaptureService
{
    public SystemAudioBackend Backend => SystemAudioBackend.WasapiLoopback;

    public IReadOnlyList<AudioDeviceInfo> GetDevices()
    {
        using var enumerator = new MMDeviceEnumerator();
        var endpoints = enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active);
        using var preferred = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
        return endpoints
            .OrderByDescending(device => device.ID == preferred.ID)
            .Select(device => new AudioDeviceInfo(device.ID, device.FriendlyName))
            .ToList();
    }

    public async IAsyncEnumerable<ReadOnlyMemory<byte>> CaptureAsync(
        string? deviceId,
        [EnumeratorCancellation] CancellationToken ct)
    {
        using var enumerator = new MMDeviceEnumerator();
        using var device = deviceId is null
            ? enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia)
            : enumerator.GetDevice(deviceId);
        using var capture = new WasapiLoopbackCapture(device);

        await foreach (var frame in WasapiPcmCapture.RunAsync(capture, ct))
            yield return frame;
    }
}
