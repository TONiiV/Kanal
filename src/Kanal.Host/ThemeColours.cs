using System;
using Avalonia;
using Avalonia.Media;

namespace Kanal.Host;

internal static class ThemeColours
{
    public static string SpeakerFallback => Hex("SpeakerFallbackColor");

    public static string TickMixed => Hex("TickMixedColor");

    private static string Hex(string key)
    {
        var app = Application.Current!;
        if (!app.TryGetResource(key, app.ActualThemeVariant, out var value) || value is not Color c)
            throw new InvalidOperationException($"Theme colour '{key}' is missing.");
        return $"#{c.R:X2}{c.G:X2}{c.B:X2}";
    }
}
