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
            SystemAudioPlatform.Windows when version >= new Version(10, 0, 19041) =>
                new(true, SystemAudioBackend.WasapiLoopback),
            SystemAudioPlatform.Windows => new(
                false,
                SystemAudioBackend.Unavailable,
                "Online meeting capture requires Windows 10 version 2004 (build 19041) or later."),
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

    // The guards only satisfy the platform analyzer; DescribeSupport owns the version cascade.
    public static ISystemAudioCaptureService? TryCreate()
    {
        var backend = Support.Backend;
        if (backend == SystemAudioBackend.WasapiLoopback && OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041))
            return new WasapiLoopbackAudioCapture();
        if (backend is SystemAudioBackend.CoreAudioProcessTap or SystemAudioBackend.ScreenCaptureKit
            && OperatingSystem.IsMacOSVersionAtLeast(13))
            return new MacSystemAudioCapture(backend);
        return null;
    }

    public static ISystemAudioCaptureService Create() =>
        TryCreate() ?? throw new PlatformNotSupportedException(Support.Reason);
}
