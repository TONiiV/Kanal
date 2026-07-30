using System.Text.Json.Nodes;

namespace Kanal.Providers.Gladia;

public sealed record GladiaOptions
{
    public required string ApiKey { get; init; }

    public string BaseUrl { get; init; } = "https://api.gladia.io";

    /// <summary>Live model to request; see Gladia docs for current names.</summary>
    public string? Model { get; init; }

    /// <summary>
    /// Merged verbatim into the session-init body. Escape hatch so config details
    /// (diarization flags, message toggles…) can be adjusted against the live docs
    /// during D0-B without a code change.
    /// </summary>
    public JsonObject? ExtraConfig { get; init; }

    public int MaxReconnectAttempts { get; init; } = 3;
}
