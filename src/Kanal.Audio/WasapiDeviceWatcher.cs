using System.Runtime.Versioning;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

namespace Kanal.Audio;

/// <summary>
/// Windows hot-plug notifications via IMMNotificationClient. Callbacks arrive on COM
/// worker threads. Like the macOS watcher this is a thin registration wrapper with no
/// seam to fake; verified by hand with a USB microphone.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WasapiDeviceWatcher : IAudioDeviceWatcher, IMMNotificationClient
{
    private readonly MMDeviceEnumerator _enumerator = new();
    private bool _disposed;

    public event Action? DevicesChanged;

    public WasapiDeviceWatcher() => _enumerator.RegisterEndpointNotificationCallback(this);

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _enumerator.UnregisterEndpointNotificationCallback(this);
        _enumerator.Dispose();
    }

    void IMMNotificationClient.OnDeviceAdded(string pwstrDeviceId) => DevicesChanged?.Invoke();

    void IMMNotificationClient.OnDeviceRemoved(string deviceId) => DevicesChanged?.Invoke();

    // A USB unplug often surfaces as NotPresent/Unplugged rather than OnDeviceRemoved.
    void IMMNotificationClient.OnDeviceStateChanged(string deviceId, DeviceState newState) =>
        DevicesChanged?.Invoke();

    void IMMNotificationClient.OnDefaultDeviceChanged(DataFlow flow, Role role, string defaultDeviceId)
    {
        if (flow == DataFlow.Capture)
            DevicesChanged?.Invoke();
    }

    void IMMNotificationClient.OnPropertyValueChanged(string pwstrDeviceId, PropertyKey key)
    {
        // Fires per property on every change, including volume moves — far too chatty to
        // re-enumerate on, and none of it adds or removes a device.
    }
}
