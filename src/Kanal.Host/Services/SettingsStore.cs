using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using Kanal.Core.Diagnostics;

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

    /// <summary>Where the export dialog opens. Null or blank falls back to Documents\Kanal.</summary>
    public string? TranscriptFolder { get; set; }

    /// <summary>Where a meeting's audio is written. Null or blank falls back to Documents\Kanal.</summary>
    public string? AudioFolder { get; set; }

    /// <summary>
    /// Whether the room's audio is written to disk while a meeting runs. On by default: the
    /// recording is the only artefact that can settle a disagreement about what was actually
    /// said, which is the situation this tool exists for. Never leaves the machine, and never
    /// runs while paused.
    /// </summary>
    public bool RecordAudio { get; set; } = true;

    /// <summary>
    /// ISO code the host's own labels and messages are shown in. Null follows the operating
    /// system, falling back to English. Nothing to do with the room's languages — the person
    /// driving the laptop is often not one of the people being translated for.
    /// </summary>
    public string? AppLanguage { get; set; }

    /// <summary>
    /// How much detail the log file keeps. Info by default — the shape of the meeting, without
    /// the frame-by-frame chatter that only helps when reproducing a fault. Written as a word
    /// rather than an ordinal: this file gets edited by hand, and "3" for Error is a trap.
    /// </summary>
    [JsonConverter(typeof(JsonStringEnumConverter<LogLevel>))]
    public LogLevel LogLevel { get; set; } = LogLevel.Info;

    /// <summary>
    /// Megabytes a single log file may reach before it is rolled over. Free-form: a support case
    /// sometimes wants one enormous file, a laptop with a full disk wants small ones.
    /// </summary>
    public int LogMaxFileSizeMb { get; set; } = DefaultLogMaxFileSizeMb;

    public const int DefaultLogMaxFileSizeMb = 10;
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

    /// <summary>
    /// Where the log files land. Beside the settings rather than in Documents: these are the
    /// application's files, not the operator's — nobody mails a log to a supplier, they open the
    /// folder from Settings when something has to be explained.
    /// </summary>
    public static string LogsPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Kanal", "logs");

    /// <summary>
    /// Where a meeting's artefacts go when nothing is configured. Documents rather than
    /// %APPDATA%: these are the operator's files, not the application's — a transcript gets
    /// mailed to a supplier and has to be findable without knowing where an app hides things.
    /// </summary>
    public static string DefaultOutputFolder { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Kanal");

    public static string ResolveTranscriptFolder(AppSettings settings) =>
        Blank(settings.TranscriptFolder) ? DefaultOutputFolder : settings.TranscriptFolder!;

    public static string ResolveAudioFolder(AppSettings settings) =>
        Blank(settings.AudioFolder) ? DefaultOutputFolder : settings.AudioFolder!;

    /// <summary>A cleared text box is not a folder: writing to "" is a failure, not a default.</summary>
    private static bool Blank(string? path) => string.IsNullOrWhiteSpace(path);

    /// <summary>Smallest rollover threshold offered, in megabytes.</summary>
    public const int MinLogMaxFileSizeMb = 1;

    /// <summary>Largest rollover threshold offered, in megabytes.</summary>
    public const int MaxLogMaxFileSizeMb = 1024;

    /// <summary>
    /// The size box takes any number of megabytes, which means it also takes 0 and -1. A
    /// threshold of zero rolls the file over on every line; the clamp is what stands between a
    /// typo and ten thousand files.
    /// </summary>
    public static int ResolveLogMaxFileSizeMb(AppSettings settings) =>
        Math.Clamp(settings.LogMaxFileSizeMb, MinLogMaxFileSizeMb, MaxLogMaxFileSizeMb);

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
        if (ResolveStoredGladiaKey(settings) is { } stored)
            return stored;

        var fromEnv = ReadEnvAllScopes(GladiaEnvVar);
        return fromEnv is null ? null : (fromEnv, null);
    }

    /// <summary>
    /// The stored half of the resolution only — no environment fallback. Hermetic tests inject
    /// this so what they assert about mode availability cannot depend on whether the machine
    /// running them happens to carry a GLADIA_API_KEY.
    /// </summary>
    public static (string Key, string? Name)? ResolveStoredGladiaKey(AppSettings settings)
    {
        var gladiaKeys = settings.ApiKeys
            .Where(k => k.Provider.Equals("gladia", StringComparison.OrdinalIgnoreCase))
            .ToList();

        var active = gladiaKeys.FirstOrDefault(k => k.Name == settings.ActiveGladiaKeyName)
                     ?? gladiaKeys.FirstOrDefault();
        return active is not null && !string.IsNullOrWhiteSpace(active.Key)
            ? (active.Key.Trim(), active.Name)
            : null;
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
