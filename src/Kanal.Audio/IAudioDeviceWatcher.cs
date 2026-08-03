namespace Kanal.Audio;

/// <summary>
/// Raises <see cref="DevicesChanged"/> when the set of input devices may have changed —
/// a microphone plugged in, unplugged, or the system default reassigned. Fires on a
/// platform thread, never the UI thread; subscribers marshal themselves. Deliberately
/// carries no payload: the one correct reaction is to re-enumerate via
/// <see cref="IAudioCaptureService.GetDevices"/>, and a device list on the event would
/// invite acting on a stale one.
/// </summary>
public interface IAudioDeviceWatcher : IDisposable
{
    event Action? DevicesChanged;
}
