namespace Kanal.Core.Updates;

public static class UpdatePlatform
{
    public static string Current { get; } =
        OperatingSystem.IsWindows() ? "windows" :
        OperatingSystem.IsMacOS() ? "macos" :
        "linux";
}
