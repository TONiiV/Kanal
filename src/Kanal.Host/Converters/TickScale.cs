using System;
using System.Globalization;
using Avalonia.Data;
using Avalonia.Data.Converters;
using Avalonia.Media.Transformation;
using Kanal.Host.ViewModels;

namespace Kanal.Host.Converters;

public static class TickScale
{
    public static readonly IValueConverter Of = new Scale();

    private sealed class Scale : IValueConverter
    {
        public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            if (value is not TickShape shape)
                return BindingOperations.DoNothing;

            var builder = TransformOperations.CreateBuilder(1);
            builder.AppendScale(shape.Reach / TranscriptRulerViewModel.TickWidth, shape.Thickness / TranscriptRulerViewModel.TickHeight);
            return builder.Build();
        }

        public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
            BindingOperations.DoNothing;
    }
}
