using System;
using System.Collections.Generic;
using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;
using Avalonia.Media.Transformation;
using Kanal.Host.ViewModels;

namespace Kanal.Host.Converters;

public static class TickScale
{
    public static readonly IMultiValueConverter Of = new Scale();

    private sealed class Scale : IMultiValueConverter
    {
        public object? Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture)
        {
            if (values is not [double reach, double thickness])
                return null;

            var builder = TransformOperations.CreateBuilder(1);
            builder.AppendScale(reach / TranscriptRulerViewModel.PeakReach, thickness / TranscriptRulerViewModel.PeakThickness);
            return builder.Build();
        }
    }
}
