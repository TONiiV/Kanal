namespace Kanal.Core.Providers;

public sealed class MtProviderException(string message, string? code = null) : Exception(message)
{
    public const string QuotaExceeded = "mt.quota";

    public const string InvalidKey = "mt.key";

    public string? Code { get; } = code;
}
