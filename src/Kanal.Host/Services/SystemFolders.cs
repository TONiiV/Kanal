using System;
using System.Diagnostics;
using System.IO;
using Kanal.Core.Diagnostics;
using Kanal.Host.Localization;

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

    // Checked first: Open recreates a missing folder and would hide a record moved outside Kanal.
    public static string OpenMeetingFolder(string? folder, Action<string> open)
    {
        if (folder is null || !Directory.Exists(folder))
            return Localizer.Instance["workspace.folderunavailable"];

        try
        {
            open(folder);
            return "";
        }
        catch (Exception ex)
        {
            Log.Warning("workspace", $"{folder} could not be opened.", ex);
            return Localizer.Instance.Format("workspace.openfolderfailed", ex.Message);
        }
    }
}
