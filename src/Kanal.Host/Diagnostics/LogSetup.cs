using System;
using System.IO;
using System.Text;
using Kanal.Core.Diagnostics;
using Kanal.Host.Services;
using NLog;
using NLog.Config;
using NLog.Targets;

namespace Kanal.Host.Diagnostics;

internal sealed class NLogSink : ILogSink
{
    private const int MaxExceptionChars = 4000;

    private const int MaxMessageChars = 2000;

    public void Write(Kanal.Core.Diagnostics.LogLevel level, string category, string message, Exception? error)
    {
        var logger = LogManager.GetLogger(string.IsNullOrWhiteSpace(category) ? "kanal" : category);
        var line = Cap(message, MaxMessageChars);
        if (error is not null)
            line += Environment.NewLine + Cap(error.ToString(), MaxExceptionChars);

        logger.Log(Translate(level), line);
    }

    private static string Cap(string text, int max) =>
        text.Length <= max ? text : text[..max] + $"… [{text.Length - max} more characters]";

    private static NLog.LogLevel Translate(Kanal.Core.Diagnostics.LogLevel level) => level switch
    {
        Kanal.Core.Diagnostics.LogLevel.Debug => NLog.LogLevel.Debug,
        Kanal.Core.Diagnostics.LogLevel.Warning => NLog.LogLevel.Warn,
        Kanal.Core.Diagnostics.LogLevel.Error => NLog.LogLevel.Error,
        _ => NLog.LogLevel.Info,
    };
}

public static class LogSetup
{
    public const string TargetName = "kanal";

    public const int RetentionDays = 14;

    // No ${exception}: that renderer has no length ceiling, so the sink caps it into the message.
    private const string LineLayout =
        "${longdate} ${level:uppercase=true:padding=-5} ${logger} ${message}";

    private static long ArchiveBytes(AppSettings settings) =>
        (long)SettingsStore.ResolveLogMaxFileSizeMb(settings) * 1024 * 1024;

    public const int DiskBudgetMb = 2048;

    private const int MinArchives = 20;

    // Age is the retention policy; the count only stops a runaway, since NLog's file-count cap
    // counts across dates and a small fixed number would undercut RetentionDays.
    private static int ArchiveCountBackstop(AppSettings settings) =>
        Math.Max(MinArchives, DiskBudgetMb / SettingsStore.ResolveLogMaxFileSizeMb(settings));

    public static LoggingConfiguration BuildConfiguration(string directory, AppSettings settings)
    {
        var target = new FileTarget(TargetName)
        {
            FileName = Path.Combine(directory, "kanal-${shortdate}.log"),
            // Same name as the live file, and no `{#}` — NLog 6 strips that rather than substituting it.
            ArchiveFileName = Path.Combine(directory, "kanal-${shortdate}.log"),
            ArchiveAboveSize = ArchiveBytes(settings),
            MaxArchiveFiles = ArchiveCountBackstop(settings),
            MaxArchiveDays = RetentionDays,
            Layout = LineLayout,
            Encoding = Encoding.UTF8,
            CreateDirs = true,
            KeepFileOpen = true,
        };

        var config = new LoggingConfiguration();
        config.AddRule(Floor(settings.LogLevel), NLog.LogLevel.Fatal, target);
        return config;
    }

    private static NLog.LogLevel Floor(Kanal.Core.Diagnostics.LogLevel level) => level switch
    {
        Kanal.Core.Diagnostics.LogLevel.Debug => NLog.LogLevel.Debug,
        Kanal.Core.Diagnostics.LogLevel.Warning => NLog.LogLevel.Warn,
        Kanal.Core.Diagnostics.LogLevel.Error => NLog.LogLevel.Error,
        _ => NLog.LogLevel.Info,
    };

    public static void Apply(AppSettings settings) => ApplyTo(SettingsStore.LogsPath, settings);

    public static void ApplyTo(string directory, AppSettings settings)
    {
        Log.Install(new NLogSink());

        try
        {
            // Eagerly: NLog defers file creation and swallows the failure, so an unwritable
            // folder would otherwise drop every line in silence.
            Directory.CreateDirectory(directory);

            var expected = Path.Combine(directory, "kanal-${shortdate}.log");
            if (LogManager.Configuration is { } live &&
                live.FindTargetByName(TargetName) is FileTarget target &&
                target.FileName.ToString() == expected)
            {
                // Updated in place. A second FileTarget over the same open file opens at a
                // stale offset and overwrites what the first one already flushed.
                target.ArchiveAboveSize = ArchiveBytes(settings);
                foreach (var rule in live.LoggingRules)
                    rule.SetLoggingLevels(Floor(settings.LogLevel), NLog.LogLevel.Fatal);
                LogManager.ReconfigExistingLoggers();
            }
            else
            {
                LogManager.Configuration = BuildConfiguration(directory, settings);
            }

            Writable = true;
        }
        catch (Exception ex)
        {
            Writable = false;
            FailureReason = ex.Message;
        }
    }

    public static bool Writable { get; private set; }

    public static string? FailureReason { get; private set; }

    public static void Flush()
    {
        try
        {
            LogManager.Flush();
        }
        catch
        {
        }
    }

    public static LogScope InstallFor(string directory, AppSettings settings)
    {
        var scope = new LogScope(Log.Sink, LogManager.Configuration);
        LogManager.Configuration = BuildConfiguration(directory, settings);
        Log.Install(new NLogSink());
        return scope;
    }

    public sealed class LogScope(ILogSink? previousSink, LoggingConfiguration? previousConfiguration)
        : IDisposable
    {
        public void Flush() => LogManager.Flush();

        public void Dispose()
        {
            LogManager.Flush();
            Log.Install(previousSink);
            LogManager.Configuration = previousConfiguration ?? new LoggingConfiguration();
        }
    }
}
