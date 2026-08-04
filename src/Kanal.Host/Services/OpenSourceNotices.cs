using System.Collections.Generic;

namespace Kanal.Host.Services;

/// <summary>
/// One project Kanal is built on. <paramref name="Packages"/> lists the NuGet ids it covers, which
/// is what a test uses to hold this list against the actual build — a credit list that quietly
/// falls behind is worse than none, because it reads as complete.
/// </summary>
public sealed record OpenSourceNotice(
    string Name,
    string License,
    string Url,
    IReadOnlyList<string> Packages);

/// <summary>
/// What is named at the bottom of Settings. An obligation before it is a feature: the MIT and BSD
/// licences here all require their notice to travel with the binary, and the people running this
/// in a meeting are the ones handing that binary around.
/// </summary>
public static class OpenSourceNotices
{
    /// <summary>Kanal's own licence, stated beside the list so the whole picture is on one screen.</summary>
    public const string OwnLicense = "MIT";

    public static IReadOnlyList<OpenSourceNotice> All { get; } =
    [
        new("Avalonia", "MIT", "https://github.com/AvaloniaUI/Avalonia",
            ["Avalonia", "Avalonia.Desktop", "Avalonia.Themes.Fluent", "AvaloniaUI.DiagnosticsSupport"]),
        new(".NET", "MIT", "https://github.com/dotnet/runtime", []),
        new("CommunityToolkit.Mvvm", "MIT", "https://github.com/CommunityToolkit/dotnet",
            ["CommunityToolkit.Mvvm"]),
        new("NLog", "BSD-3-Clause", "https://github.com/NLog/NLog", ["NLog"]),
        new("QRCoder", "MIT", "https://github.com/codebude/QRCoder", ["QRCoder"]),
        new("NAudio", "MIT", "https://github.com/naudio/NAudio", ["NAudio.Wasapi"]),
        new("LLamaSharp", "MIT", "https://github.com/SciSharp/LLamaSharp",
            ["LLamaSharp", "LLamaSharp.Backend.Cpu"]),
        // Shipped inside the LLamaSharp backend rather than as a package of its own, and the
        // reason local translation runs at all.
        new("llama.cpp", "MIT", "https://github.com/ggml-org/llama.cpp", []),
        // Loaded by the mobile page, not by the host — but it is the only third-party code that
        // runs on a participant's phone, so it belongs on the same list.
        new("supabase-js", "MIT", "https://github.com/supabase/supabase-js", []),
    ];
}
