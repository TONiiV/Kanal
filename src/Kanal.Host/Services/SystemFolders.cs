using System;
using System.Diagnostics;
using System.IO;

namespace Kanal.Host.Services;

/// <summary>
/// Shows a folder in whatever the machine uses to browse files. One line of platform branching,
/// kept out of the view model so a headless test never launches Explorer.
/// </summary>
public static class SystemFolders
{
    /// <summary>
    /// Opens <paramref name="path"/>, creating it first: a log folder nothing has written to yet
    /// still has to open, or the button appears broken on a fresh install.
    /// </summary>
    public static void Open(string path)
    {
        Directory.CreateDirectory(path);

        var command = OperatingSystem.IsWindows() ? "explorer.exe"
            : OperatingSystem.IsMacOS() ? "open"
            : "xdg-open";

        // ArgumentList rather than a command line: the profile path contains the operator's name,
        // and names contain spaces.
        var start = new ProcessStartInfo(command) { UseShellExecute = false };
        start.ArgumentList.Add(path);
        // Disposed, not discarded: the file manager outlives this call either way, and one
        // undisposed handle per click adds up over a long-running host.
        using var process = Process.Start(start);
    }
}
