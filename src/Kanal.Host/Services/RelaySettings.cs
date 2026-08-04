using System;

namespace Kanal.Host.Services;

/// <summary>
/// Relay + mobile-page endpoints. Defaults point at the shared Supabase project
/// (publishable key — public by design) and the GitHub Pages deployment of
/// web/index.html. A custom web deployment must carry the same public Supabase
/// configuration as its host override; the join URL itself carries no infrastructure.
/// </summary>
public sealed record RelaySettings(string SupabaseUrl, string PublishableKey, string WebAppUrl)
{
    public const string DefaultSupabaseUrl = "https://muwffgozlmjafsoykqfr.supabase.co";

    public const string DefaultPublishableKey =
        "sb_publishable_oXkDmUJWWh6R0xbR2dpD-A_txxb8O35";

    public const string DefaultWebAppUrl = "https://toniiv.github.io/Kanal/";

    public static RelaySettings FromEnvironment() => new(
        SettingsStore.ReadEnvAllScopes("KANAL_SUPABASE_URL") ?? DefaultSupabaseUrl,
        SettingsStore.ReadEnvAllScopes("KANAL_SUPABASE_PUBLISHABLE_KEY")
            ?? SettingsStore.ReadEnvAllScopes("KANAL_SUPABASE_ANON_KEY")
            ?? DefaultPublishableKey,
        SettingsStore.ReadEnvAllScopes("KANAL_WEB_URL") ?? DefaultWebAppUrl);

    public string BuildJoinUrl(string roomId, string verificationKey) =>
        $"{WebAppUrl.TrimEnd('#')}#room={Uri.EscapeDataString(roomId)}&vk={Uri.EscapeDataString(verificationKey)}";
}
