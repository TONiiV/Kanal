namespace Kanal.Audio;

public static class SystemAudioCaptureFactory
{
    public static SystemAudioSupport Support
    {
        get
        {
            if (OperatingSystem.IsWindows())
                return DescribeSupport(SystemAudioPlatform.Windows, Environment.OSVersion.Version);
            if (OperatingSystem.IsMacOS())
                return DescribeSupport(SystemAudioPlatform.MacOS, Environment.OSVersion.Version);
            return DescribeSupport(SystemAudioPlatform.Other, Environment.OSVersion.Version);
        }
    }

    public static SystemAudioSupport DescribeSupport(SystemAudioPlatform platform, Version version) =>
        platform switch
        {
            SystemAudioPlatform.Windows => new(true, SystemAudioBackend.WasapiLoopback),
            SystemAudioPlatform.MacOS when version >= new Version(14, 2) =>
                new(true, SystemAudioBackend.CoreAudioProcessTap),
            SystemAudioPlatform.MacOS when version >= new Version(13, 0) =>
                new(true, SystemAudioBackend.ScreenCaptureKit),
            SystemAudioPlatform.MacOS => new(
                false,
                SystemAudioBackend.Unavailable,
                "Online meeting capture requires macOS 13 or later."),
            _ => new(
                false,
                SystemAudioBackend.Unavailable,
                "Native computer-audio capture is available on Windows and macOS."),
        };

    // The version cascade lives in DescribeSupport alone; the guards below only satisfy the
    // platform analyzer, which cannot see through Support.
    public static ISystemAudioCaptureService? TryCreate()
    {
        var backend = Support.Backend;
        if (backend == SystemAudioBackend.WasapiLoopback && OperatingSystem.IsWindows())
            return new WasapiLoopbackAudioCapture();
        if (backend is SystemAudioBackend.CoreAudioProcessTap or SystemAudioBackend.ScreenCaptureKit
            && OperatingSystem.IsMacOSVersionAtLeast(13))
            return new MacSystemAudioCapture(backend);
        return null;
    }

    public static ISystemAudioCaptureService Create() =>
        TryCreate() ?? throw new PlatformNotSupportedException(Support.Reason);
}
