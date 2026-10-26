using System;
using System.Diagnostics;

namespace Kanal.Host.Services;

public static class SystemLinks
{
    public static void Open(Uri link)
    {
        if (link.Scheme != Uri.UriSchemeHttps)
            throw new ArgumentException("Only HTTPS links open from the app.", nameof(link));

        var command = OperatingSystem.IsWindows() ? "explorer.exe"
            : OperatingSystem.IsMacOS() ? "open"
            : "xdg-open";

        var start = new ProcessStartInfo(command) { UseShellExecute = false };
        start.ArgumentList.Add(link.AbsoluteUri);
        using var process = Process.Start(start);
    }
}
