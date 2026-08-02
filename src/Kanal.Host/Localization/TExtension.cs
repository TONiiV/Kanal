using System;
using Avalonia.Data;
using Avalonia.Markup.Xaml;

namespace Kanal.Host.Localization;

/// <summary>
/// <c>Text="{l:T transport.start}"</c>. Produces a binding rather than a value, so a language
/// change reaches windows that are already open — the operator switches mid-meeting and the
/// screen follows, without restarting a room.
/// </summary>
public sealed class TExtension : MarkupExtension
{
    public TExtension()
    {
    }

    public TExtension(string key) => Key = key;

    public string Key { get; set; } = "";

    public override object ProvideValue(IServiceProvider serviceProvider) =>
        new Binding($"[{Key}]")
        {
            Source = Localizer.Instance,
            Mode = BindingMode.OneWay,
        };
}
