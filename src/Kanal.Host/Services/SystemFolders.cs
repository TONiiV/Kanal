using System;
using System.Diagnostics;
using System.IO;

namespace Kanal.Host.Services;

public static class SystemFolders
{
    public static void Open(string path)
    {
        Directory.CreateDirectory(path);

        var command = OperatingSystem.IsWindows() ? "explorer.exe"
            : OperatingSystem.IsMacOS() ? "open"
            : "xdg-open";

        var start = new ProcessStartInfo(command) { UseShellExecute = false };
        start.ArgumentList.Add(path);
        using var process = Process.Start(start);
    }
}
