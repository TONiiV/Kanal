using System;
using System.Runtime.InteropServices;

namespace Kanal.Host.Services;

public static class SystemMotion
{
    private const uint GetClientAreaAnimation = 0x1042;

    // Windows only: no portable Avalonia switch exists, and macOS is unprobed.
    public static bool Reduced => OperatingSystem.IsWindows() && !ClientAreaAnimates();

    private static bool ClientAreaAnimates() =>
        !SystemParametersInfo(GetClientAreaAnimation, 0, out var on, 0) || on != 0;

    [DllImport("user32.dll", EntryPoint = "SystemParametersInfoW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SystemParametersInfo(uint action, uint param, out int value, uint winIni);
}
