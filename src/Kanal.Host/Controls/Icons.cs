using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Xml.Linq;
using Avalonia.Media;
using Avalonia.Platform;
using Kanal.Host.Services;

namespace Kanal.Host.Controls;

/// <summary>
/// The chrome's glyphs, each one an SVG under <c>Assets/Icons</c> that a designer can open,
/// loaded as geometry so a <see cref="Avalonia.Controls.Shapes.Path"/> keeps taking its fill from
/// the style that owns it — which an <c>SvgImage</c> would not.
/// </summary>
public static class Icons
{
    private const string Folder = "avares://Kanal.Host/Assets/Icons";

    private static readonly ConcurrentDictionary<string, Geometry> Loaded = new();

    public static IReadOnlyList<string> Names { get; } =
        AssetLoader.GetAssets(new Uri(Folder), null)
            .Select(uri => uri.Segments[^1])
            .Where(file => file.EndsWith(".svg", StringComparison.OrdinalIgnoreCase))
            .Select(file => file[..^4])
            .Order(StringComparer.Ordinal)
            .ToList();

    public static Geometry Of(string name) => Loaded.GetOrAdd(name, Load);

    /// <summary>
    /// One mark per mode. The shape is where transcription runs; an arrow on it means the second
    /// stage runs on the other side, pointing the way the work is handed over.
    /// </summary>
    public static Geometry Mode(PipelineModeId mode) => Of(mode switch
    {
        PipelineModeId.CloudCloud => "cloud",
        PipelineModeId.CloudLocal => "cloud-down",
        PipelineModeId.LocalCloud => "local-up",
        PipelineModeId.LocalLocal => "local",
        _ => "script",
    });

    private static Geometry Load(string name)
    {
        var uri = new Uri($"{Folder}/{name}.svg");
        if (!AssetLoader.Exists(uri))
        {
            throw new ArgumentException($"there is no Assets/Icons/{name}.svg", nameof(name));
        }

        using var stream = AssetLoader.Open(uri);
        var svg = XDocument.Load(stream).Root
            ?? throw new ArgumentException($"Assets/Icons/{name}.svg is empty", nameof(name));

        var paths = svg.Descendants().Where(node => node.Name.LocalName == "path").ToList();
        if (paths.Count == 0)
        {
            throw new ArgumentException($"Assets/Icons/{name}.svg draws no path", nameof(name));
        }

        var rules = paths.Select(path => (string?)path.Attribute("fill-rule") ?? "nonzero").Distinct().ToList();
        if (rules.Count > 1)
        {
            throw new ArgumentException($"Assets/Icons/{name}.svg mixes fill rules", nameof(name));
        }

        var box = ViewBox(svg, name);
        // Two draw-nothing move-tos at the view box corners. Avalonia measures a Path by its ink,
        // then pins the uniformly scaled result to the top-left of the control's box, so an icon
        // narrower than its box drifts left and up by the slack; carrying the corners makes the
        // measured bounds the view box, and the drawing lands where the SVG put it.
        var corners = string.Create(CultureInfo.InvariantCulture,
            $"M{box.X},{box.Y} M{box.X + box.Width},{box.Y + box.Height} ");
        var fill = rules[0] == "evenodd" ? "F0 " : "F1 ";

        return Geometry.Parse(fill + corners + string.Join(' ', paths.Select(p => (string?)p.Attribute("d"))));
    }

    private static (double X, double Y, double Width, double Height) ViewBox(XElement svg, string name)
    {
        var parts = ((string?)svg.Attribute("viewBox") ?? "")
            .Split([' ', ','], StringSplitOptions.RemoveEmptyEntries)
            .Select(part => double.Parse(part, CultureInfo.InvariantCulture))
            .ToList();

        return parts.Count == 4
            ? (parts[0], parts[1], parts[2], parts[3])
            : throw new ArgumentException($"Assets/Icons/{name}.svg has no viewBox", nameof(name));
    }
}
