using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Avalonia.Data.Converters;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Kanal.Host.Localization;

namespace Kanal.Host.Controls;

public static class ProjectIcons
{
    public const string DefaultGlyph = "folder";

    public static IReadOnlyList<string> Glyphs { get; } =
        [DefaultGlyph, "people", "microphone", "screen", "local", "cloud", "script", "pencil"];

    public static readonly IValueConverter Glyph = new FuncValueConverter<string?, Geometry>(name =>
        Icons.Of(name is not null && Glyphs.Contains(name) ? name : DefaultGlyph));

    public static readonly IValueConverter Image = new FuncValueConverter<string?, Bitmap?>(Load);

    public static readonly IValueConverter Label =
        new FuncValueConverter<string?, string>(name => Localizer.Instance["workspace.glyph." + name]);

    // Read through a closed stream: a bitmap holding the file open would block reset and removal on Windows.
    private static Bitmap? Load(string? path)
    {
        if (path is null)
            return null;

        try
        {
            using var stream = File.OpenRead(path);
            return Bitmap.DecodeToWidth(stream, 64);
        }
        catch (Exception)
        {
            return null;
        }
    }
}
