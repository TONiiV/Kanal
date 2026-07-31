using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace Kanal.Host.Services;

public sealed record ApiKeyEntry(string Name, string Provider, string Key);

public sealed class AppSettings
{
    public List<ApiKeyEntry> ApiKeys { get; set; } = new();

    /// <summary>Name of the key to use for Gladia; null falls back to the first Gladia entry, then the env var.</summary>
    public string? ActiveGladiaKeyName { get; set; }

    /// <summary>
    /// Catalog id of the local translation model to run in-process, or null for
    /// the default: translation by the cloud ASR provider (Gladia).
    /// </summary>
    public string? ActiveTranslationModelId { get; set; }
}

/// <summary>
/// Plain-JSON settings in the user profile (%APPDATA%/Kanal/settings.json).
/// Key resolution order: active named key → any stored Gladia key → GLADIA_API_KEY
/// env var (process, then user, then machine scope, so a freshly set system
/// variable is found without relogging).
/// </summary>
public static class SettingsStore
{
    public const string GladiaEnvVar = "GLADIA_API_KEY";

    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public static string SettingsPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Kanal", "settings.json");

    /// <summary>Where downloaded GGUF translation models live.</summary>
    public static string ModelsPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Kanal", "models");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsPath), Options)
                       ?? new AppSettings();
        }
        catch
        {
            // corrupt settings — start fresh rather than crash the host
        }

        return new AppSettings();
    }

    public static void Save(AppSettings settings)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
        File.WriteAllText(SettingsPath, JsonSerializer.Serialize(settings, Options));
    }

    /// <summary>
    /// Resolve the cloud key and where it came from. <paramref name="Name"/> is the stored
    /// entry's name, or null when the key came from the environment — the caller phrases it,
    /// because the main screen must not print a vendor's name or its env var.
    /// </summary>
    public static (string Key, string? Name)? ResolveGladiaKey(AppSettings settings)
    {
        var gladiaKeys = settings.ApiKeys
            .Where(k => k.Provider.Equals("gladia", StringComparison.OrdinalIgnoreCase))
            .ToList();

        var active = gladiaKeys.FirstOrDefault(k => k.Name == settings.ActiveGladiaKeyName)
                     ?? gladiaKeys.FirstOrDefault();
        if (active is not null && !string.IsNullOrWhiteSpace(active.Key))
            return (active.Key.Trim(), active.Name);

        var fromEnv = ReadEnvAllScopes(GladiaEnvVar);
        return fromEnv is null ? null : (fromEnv, null);
    }

    public static string? ReadEnvAllScopes(string name)
    {
        var value = Environment.GetEnvironmentVariable(name);
        if (string.IsNullOrWhiteSpace(value) && OperatingSystem.IsWindows())
        {
            value = Environment.GetEnvironmentVariable(name, EnvironmentVariableTarget.User)
                    ?? Environment.GetEnvironmentVariable(name, EnvironmentVariableTarget.Machine);
        }

        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }
}
