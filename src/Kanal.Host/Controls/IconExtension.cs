using System;
using Avalonia.Markup.Xaml;
using Avalonia.Media;

namespace Kanal.Host.Controls;

public sealed class IconExtension : MarkupExtension
{
    public IconExtension()
    {
    }

    public IconExtension(string name) => Name = name;

    public string Name { get; set; } = string.Empty;

    public override object ProvideValue(IServiceProvider serviceProvider) => Icons.Of(Name);
}
