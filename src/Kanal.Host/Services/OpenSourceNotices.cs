using System.Collections.Generic;

namespace Kanal.Host.Services;

public sealed record OpenSourceNotice(
    string Name,
    string License,
    string Url,
    IReadOnlyList<string> Packages);

// An index, not a notice: MIT and BSD-3 ask for the licence text itself to travel with the binary,
// and that is not shipped yet — see docs/PROGRESS.md.
public static class OpenSourceNotices
{
    public const string OwnLicense = "MIT";

    public static IReadOnlyList<OpenSourceNotice> All { get; } =
    [
        new("Avalonia", "MIT", "https://github.com/AvaloniaUI/Avalonia",
            ["Avalonia", "Avalonia.Desktop", "Avalonia.Themes.Fluent"]),
        new("SkiaSharp / HarfBuzzSharp", "MIT", "https://github.com/mono/SkiaSharp", []),
        new("Skia", "BSD-3-Clause", "https://github.com/google/skia", []),
        new("HarfBuzz", "Old MIT", "https://github.com/harfbuzz/harfbuzz", []),
        // Ships as av_libglesv2.dll in every Windows build; its licence names binary redistribution.
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
        new("llama.cpp", "MIT", "https://github.com/ggml-org/llama.cpp", []),
        new("OpenCC", "Apache-2.0", "https://github.com/BYVoid/OpenCC", []),
    ];
}
