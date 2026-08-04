using System;
using System.Reflection;

namespace Kanal.Host.Services;

/// <summary>
/// Which build this is. Printed in Settings and on the log's first line, so a log sent by an
/// operator can be read against the code that wrote it.
/// </summary>
public static class AppVersion
{
    public static string Current { get; } = Read();

    private static string Read()
    {
        var assembly = typeof(AppVersion).Assembly;
        var informational = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        var version = string.IsNullOrWhiteSpace(informational)
            ? assembly.GetName().Version?.ToString(fieldCount: 3)
            : informational;

        if (string.IsNullOrWhiteSpace(version))
            return "0.0.0";

        // The SDK appends "+<commit sha>" to the informational version. Useful in a log line,
        // noise in a heading — the heading is where this is read.
        var build = version.IndexOf('+');
        return build < 0 ? version : version[..build];
    }
}
