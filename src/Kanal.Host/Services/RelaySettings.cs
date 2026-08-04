using System;

namespace Kanal.Host.Services;

/// <summary>
/// Public gateway/mobile endpoints plus the host bootstrap capability. Neither the repository
/// nor a built client contains Supabase configuration; the operator provisions these two relay
/// values outside the build and the QR carries only a short-lived reader ticket.
/// </summary>
public sealed record RelaySettings(string? GatewayUrl, string? HostToken, string WebAppUrl)
{
    public const string DefaultWebAppUrl = "https://toniiv.github.io/Kanal/";

    public static RelaySettings FromEnvironment() => new(
        SettingsStore.ReadEnvAllScopes("KANAL_RELAY_URL"),
        SettingsStore.ReadEnvAllScopes("KANAL_RELAY_HOST_TOKEN"),
        SettingsStore.ReadEnvAllScopes("KANAL_WEB_URL") ?? DefaultWebAppUrl);

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(GatewayUrl) && !string.IsNullOrWhiteSpace(HostToken);

    public string BuildJoinUrl(
        string inviteTicket,
        string roomId,
        string verificationKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(GatewayUrl);
        if (!Uri.TryCreate(WebAppUrl, UriKind.Absolute, out var webApp) ||
            (!string.Equals(webApp.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) &&
             !(string.Equals(webApp.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) &&
               webApp.IsLoopback)) ||
            !string.IsNullOrEmpty(webApp.UserInfo) ||
            !string.IsNullOrEmpty(webApp.Fragment))
            throw new InvalidOperationException(
                "The mobile page must use HTTPS and no fragment (HTTP is allowed only on localhost).");

        return $"{WebAppUrl.TrimEnd('#')}#relay={Uri.EscapeDataString(GatewayUrl)}" +
               $"&ticket={Uri.EscapeDataString(inviteTicket)}" +
               $"&room={Uri.EscapeDataString(roomId)}" +
               $"&vk={Uri.EscapeDataString(verificationKey)}";
    }
}
