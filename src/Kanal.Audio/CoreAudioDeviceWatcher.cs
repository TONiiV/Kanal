using System.Runtime.Versioning;

namespace Kanal.Audio;

// The HAL registration has no seam to fake: verify by hand-plugging a USB microphone with Settings open.
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
