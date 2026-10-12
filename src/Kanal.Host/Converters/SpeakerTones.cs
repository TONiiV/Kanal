using System;
using System.Collections.Generic;
using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;
using Avalonia.Styling;
using Kanal.Core.Room;

namespace Kanal.Host.Converters;

public static class SpeakerTones
{
    public static readonly IMultiValueConverter Name = new NameTone();

    private sealed class NameTone : IMultiValueConverter
    {
        public object? Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture)
        {
            if (values is not [string { Length: 7 } hex, ThemeVariant variant])
                return null;
            var ink = variant == ThemeVariant.Dark ? SpeakerTone.DarkInk : SpeakerTone.LightInk;
            return Brush.Parse(SpeakerTone.Text(hex, ink));
        }
    }
}
