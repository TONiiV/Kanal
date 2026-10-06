namespace Kanal.Host.Services;

public readonly record struct LightFrame(double X, double Y, double Width, double Height);

public static class TrafficLightLayout
{
    public const double Scale = 1.2;
    public const double FirstCenter = 20;
    public const double Pitch = 24;

    public static LightFrame Frame(int index, double barHeight, double nativeWidth, double nativeHeight)
    {
        var width = nativeWidth * Scale;
        var height = nativeHeight * Scale;
        return new LightFrame(FirstCenter + index * Pitch - width / 2, (barHeight - height) / 2, width, height);
    }
}
