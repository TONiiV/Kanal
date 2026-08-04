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

    /// <remarks>
    /// <c>Packages</c> names the NuGet ids a notice covers, which is what a test uses to hold the
    /// list against the actual build. Entries with an empty list are things no package reference
    /// mentions and no test can find on its own — code that arrives inside another package, or a
    /// data file compiled into the assembly — so they are the ones to check by hand when a
    /// dependency changes.
    /// </remarks>
    public static IReadOnlyList<OpenSourceNotice> All { get; } =
    [
        new("Avalonia", "MIT", "https://github.com/AvaloniaUI/Avalonia",
            ["Avalonia", "Avalonia.Desktop", "Avalonia.Themes.Fluent"]),
        new("Avalonia DevTools", "MIT",
            "https://www.nuget.org/packages/AvaloniaUI.DiagnosticsSupport",
            ["AvaloniaUI.DiagnosticsSupport"]),
        // Avalonia draws through these and ships them beside itself; Skia is the C++ library the
        // wrapper binds to, and its BSD notice has to travel too.
        new("SkiaSharp / HarfBuzzSharp", "MIT", "https://github.com/mono/SkiaSharp", []),
        new("Skia", "BSD-3-Clause", "https://github.com/google/skia", []),
        new("MicroCom", "MIT", "https://github.com/kekekeks/MicroCom", []),
        new("Tmds.DBus", "MIT", "https://github.com/tmds/Tmds.DBus", []),
        new(".NET", "MIT", "https://github.com/dotnet/runtime", []),
        new("Microsoft.Extensions", "MIT", "https://github.com/dotnet/extensions", []),
        new("Reactive Extensions for .NET", "MIT", "https://github.com/dotnet/reactive", []),
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
        // Not a package at all: OpenCC's conversion table is compiled into Kanal.Core, and its
        // licence is the one here that explicitly requires the notice to travel with the work.
        new("OpenCC", "Apache-2.0", "https://github.com/BYVoid/OpenCC", []),
    ];
}
