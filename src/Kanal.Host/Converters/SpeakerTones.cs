using Avalonia.Data.Converters;
using Avalonia.Media;
using Kanal.Core.Room;

namespace Kanal.Host.Converters;

public static class SpeakerTones
{
    public static readonly IValueConverter OnLight =
        new FuncValueConverter<string?, IBrush?>(hex =>
            hex is { Length: 7 } ? Brush.Parse(SpeakerTone.Text(hex, SpeakerTone.LightInk)) : null);
}
