using System;
using System.Reflection;

namespace Kanal.Host.Services;

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

        var build = version.IndexOf('+');
        return build < 0 ? version : version[..build];
    }
}
