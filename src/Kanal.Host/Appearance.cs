using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
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

    private static readonly string[] OnBrandMirrors =
    [
        "CheckBoxCheckGlyphForegroundChecked", "CheckBoxCheckGlyphForegroundCheckedPointerOver",
        "CheckBoxCheckGlyphForegroundCheckedPressed", "CheckBoxCheckGlyphForegroundIndeterminate",
        "CheckBoxCheckGlyphForegroundIndeterminatePointerOver", "CheckBoxCheckGlyphForegroundIndeterminatePressed",
        "RadioButtonCheckGlyphFill", "RadioButtonCheckGlyphFillPointerOver", "RadioButtonCheckGlyphFillPressed",
        "ToggleSwitchKnobFillOn", "ToggleSwitchKnobFillOnPointerOver", "ToggleSwitchKnobFillOnPressed",
    ];

    public static ColourScheme Scheme { get; private set; } = ColourScheme.Light;

    public static AccentTheme Theme { get; private set; } = AccentThemes.Kanal;

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

    public static void Apply(AccentTheme theme)
    {
        Theme = theme;
        if (Application.Current is { } app)
        {
            Write(app, ThemeVariant.Light, theme.Light, dark: false);
            Write(app, ThemeVariant.Dark, theme.Dark, dark: true);
        }

        Refresh();
    }

    private static void Write(Application app, ThemeVariant variant, AccentVariant accent, bool dark)
    {
        var dictionary = (ResourceDictionary)app.Resources.ThemeDictionaries[variant];
        var brushes = new Dictionary<string, string>
        {
            ["Brand"] = accent.Brand, ["BrandHover"] = accent.Hover, ["BrandPressed"] = accent.Pressed,
            ["BrandWash"] = accent.Wash, ["OnBrand"] = accent.OnBrand,
        };
        foreach (var key in OnBrandMirrors)
            brushes[key] = accent.OnBrand;
        foreach (var (key, hex) in brushes)
            dictionary[key] = new SolidColorBrush(Color.Parse(hex));

        string[] ramp = dark
            ? [accent.Brand, accent.Hover, accent.Pressed, accent.Wash, accent.RampA, accent.RampB, accent.RampC]
            : [accent.Brand, accent.RampA, accent.RampB, accent.Wash, accent.Hover, accent.Pressed, accent.RampC];
        string[] names = ["", "Light1", "Light2", "Light3", "Dark1", "Dark2", "Dark3"];
        for (var i = 0; i < names.Length; i++)
            dictionary[OwnRamp + names[i]] = Color.Parse(ramp[i]);
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
