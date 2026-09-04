namespace Kanal.Audio;

/// <summary>The only platform/version switch for native computer-audio capture.</summary>
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

    public static ISystemAudioCaptureService? TryCreate()
    {
        if (OperatingSystem.IsWindows())
            return new WasapiLoopbackAudioCapture();
        if (OperatingSystem.IsMacOSVersionAtLeast(14, 2))
            return new MacSystemAudioCapture(SystemAudioBackend.CoreAudioProcessTap);
        if (OperatingSystem.IsMacOSVersionAtLeast(13))
            return new MacSystemAudioCapture(SystemAudioBackend.ScreenCaptureKit);
        return null;
    }

    public static ISystemAudioCaptureService Create() =>
        TryCreate() ?? throw new PlatformNotSupportedException(Support.Reason);
}
