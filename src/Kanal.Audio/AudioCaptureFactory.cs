using System.Runtime.InteropServices;

namespace Kanal.Audio;

/// <summary>
/// The one place that knows which capture backend a platform gets. Callers depend on
/// <see cref="IAudioCaptureService"/> only.
/// </summary>
public static class AudioCaptureFactory
{
    public static bool IsSupported => OperatingSystem.IsWindows() || OperatingSystem.IsMacOS();

    /// <summary>Backend for the running platform, or null where capture is unavailable.</summary>
    public static IAudioCaptureService? TryCreate()
    {
        if (OperatingSystem.IsWindows())
            return new WasapiAudioCapture();
        if (OperatingSystem.IsMacOS())
            return new CoreAudioCapture();
        return null;
    }

    public static IAudioCaptureService Create() =>
        TryCreate() ?? throw new PlatformNotSupportedException(
            $"No audio capture backend for {RuntimeInformation.OSDescription}.");
}
