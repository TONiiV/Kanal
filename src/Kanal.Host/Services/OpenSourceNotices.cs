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
/// What is named at the bottom of Settings: an index of what Kanal is assembled from — project,
/// licence, and where to read it.
/// </summary>
/// <remarks>
/// An index is not a notice. MIT and BSD-3 ask for the copyright and permission text itself to
/// travel with a binary, and Apache-2.0 for a copy of the licence; none of that is shipped yet, so
/// nothing here should be described as discharging those terms. Naming a licence that cannot be
/// substantiated is worse than omitting the entry — every one below was checked against the
/// upstream project, and the one that could not be (a debugging aid, excluded from Release
/// anyway) was removed rather than guessed at.
/// </remarks>
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
        // Avalonia draws through these and ships them beside itself. The wrappers are MIT; the
        // native libraries they bind to are separate works with their own notices, and Skia in
        // turn links FreeType, libpng, libjpeg-turbo, Expat, zlib and Wuffs — see the notice file.
        new("SkiaSharp / HarfBuzzSharp", "MIT", "https://github.com/mono/SkiaSharp", []),
        new("Skia", "BSD-3-Clause", "https://github.com/google/skia", []),
        new("HarfBuzz", "Old MIT", "https://github.com/harfbuzz/harfbuzz", []),
        // Ships in every Windows build as av_libglesv2.dll, and its licence is explicit that a
        // binary redistribution reproduces the notice.
        new("ANGLE", "BSD-3-Clause", "https://github.com/google/angle", []),
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
