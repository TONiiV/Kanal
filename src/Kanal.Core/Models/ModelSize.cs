namespace Kanal.Core.Models;

public static class ModelSize
{
    public static string Label(long bytes) => $"{bytes / (1024.0 * 1024 * 1024):0.0} GB";
}
