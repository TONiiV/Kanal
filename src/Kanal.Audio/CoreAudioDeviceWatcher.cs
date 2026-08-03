using System.Runtime.Versioning;

namespace Kanal.Audio;

/// <summary>
/// macOS hot-plug notifications: a property listener on the HAL's system object (see
/// <see cref="MacCoreAudio.AddDeviceTopologyListener"/> for which properties). Thin by
/// design — the registration itself has no seam to fake, so it is verified by hand:
/// plug and unplug a USB microphone while the main window and Settings are open.
/// </summary>
[SupportedOSPlatform("macos")]
public sealed class CoreAudioDeviceWatcher : IAudioDeviceWatcher
{
    // CoreAudio holds only the native thunk; this field keeps the delegate alive until removal.
    private readonly MacCoreAudio.AudioObjectPropertyListener _listener;
    private bool _disposed;

    public event Action? DevicesChanged;

    public CoreAudioDeviceWatcher()
    {
        _listener = (_, _, _, _) =>
        {
            DevicesChanged?.Invoke();
            return 0;
        };
        MacCoreAudio.AddDeviceTopologyListener(_listener);
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        MacCoreAudio.RemoveDeviceTopologyListener(_listener);
    }
}
