using System;
using Avalonia;
using Avalonia.Media;
using Avalonia.Threading;

namespace Kanal.Host;

public static class ThemeColours
{
    public static string SpeakerFallback => Hex("SpeakerFallbackColor") ?? "#4C5C68";

    public static string TickMixed => Hex("TickMixedColor") ?? "#7C8A93";

    private static string? Hex(string key)
    {
        var app = Application.Current;
        if (app is null || !Dispatcher.UIThread.CheckAccess())
            return null;
        if (!app.TryGetResource(key, app.ActualThemeVariant, out var value) || value is not Color c)
            throw new InvalidOperationException($"Theme colour '{key}' is missing.");
        return $"#{c.R:X2}{c.G:X2}{c.B:X2}";
    }
}
