using System;

namespace Kanal.Host.Services;

/// <summary>
/// Relay + mobile-page endpoints. Defaults point at the shared Supabase project
/// (anon key — public by design, it ships inside every join QR anyway) and the
/// GitHub Pages deployment of web/index.html. Env vars override each piece, so
/// swapping to a dedicated Supabase project or Vercel hosting is config-only.
/// </summary>
public sealed record RelaySettings(string SupabaseUrl, string AnonKey, string WebAppUrl)
{
    public const string DefaultSupabaseUrl = "https://muwffgozlmjafsoykqfr.supabase.co";

    public const string DefaultAnonKey =
        "eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJpc3MiOiJzdXBhYmFzZSIsInJlZiI6Im11d2ZmZ296bG1qYWZzb3lrcWZyIiwicm9sZSI6ImFub24iLCJpYXQiOjE3NzU5MjQ3OTIsImV4cCI6MjA5MTUwMDc5Mn0.GzYODPn7V09-8LgFlJ3woCDMouueoTmeOXPdgp9lz40";

    public const string DefaultWebAppUrl = "https://toniiv.github.io/Kanal/";

    public string SupabaseRef => new Uri(SupabaseUrl).Host.Split('.')[0];

    public static RelaySettings FromEnvironment() => new(
        SettingsStore.ReadEnvAllScopes("KANAL_SUPABASE_URL") ?? DefaultSupabaseUrl,
        SettingsStore.ReadEnvAllScopes("KANAL_SUPABASE_ANON_KEY") ?? DefaultAnonKey,
        SettingsStore.ReadEnvAllScopes("KANAL_WEB_URL") ?? DefaultWebAppUrl);

    public string BuildJoinUrl(string roomId) =>
        $"{WebAppUrl}?room={Uri.EscapeDataString(roomId)}&sbref={Uri.EscapeDataString(SupabaseRef)}&key={Uri.EscapeDataString(AnonKey)}";
}
