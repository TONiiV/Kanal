using System;
using System.Linq;
using Avalonia;
using Avalonia.Platform;
using Avalonia.Styling;
using Avalonia.Threading;
using Kanal.Host.Services;

namespace Kanal.Host;

public static class Appearance
{
    private const string FluentRamp = "SystemAccentColor";
    private const string OwnRamp = "AccentRamp";

    private static IPlatformSettings? _platform;
    private static PlatformThemeVariant _system = PlatformThemeVariant.Light;

    public static ColourScheme Scheme { get; private set; } = ColourScheme.Light;

    public static IPlatformSettings? Platform
    {
        get => _platform;
        set
        {
            if (_platform is not null)
                _platform.ColorValuesChanged -= OnSystemChanged;
            _platform = value;
            _system = value?.GetColorValues().ThemeVariant ?? PlatformThemeVariant.Light;
            if (_platform is not null)
                _platform.ColorValuesChanged += OnSystemChanged;
            Refresh();
        }
    }

    public static void Apply(ColourScheme scheme)
    {
        Scheme = scheme;
        Refresh();
    }

    private static void OnSystemChanged(object? sender, PlatformColorValues values) =>
        SystemChanged(values.ThemeVariant);

    public static void SystemChanged(PlatformThemeVariant variant)
    {
        _system = variant;
        if (Dispatcher.UIThread.CheckAccess())
            Refresh();
        else
            Dispatcher.UIThread.Post(Refresh);
    }

    private static void Refresh()
    {
        if (Application.Current is not { } app)
            return;

        var dark = Scheme == ColourScheme.Dark
                   || (Scheme == ColourScheme.System && _system == PlatformThemeVariant.Dark);
        var variant = dark ? ThemeVariant.Dark : ThemeVariant.Light;
        app.RequestedThemeVariant = variant;

        // FluentTheme reads SystemAccentColor* only from the root dictionary, never from a theme dictionary.
        foreach (var key in app.Resources.Keys.OfType<string>().Where(k => k.StartsWith(FluentRamp, StringComparison.Ordinal)).ToList())
        {
            if (app.TryGetResource(OwnRamp + key[FluentRamp.Length..], variant, out var colour))
                app.Resources[key] = colour;
        }
    }
}
