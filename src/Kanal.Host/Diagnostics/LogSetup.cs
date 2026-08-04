using System;
using System.IO;
using System.Text;
using Kanal.Core.Diagnostics;
using Kanal.Host.Services;
using NLog;
using NLog.Config;
using NLog.Targets;

namespace Kanal.Host.Diagnostics;

/// <summary>Writes <see cref="Log"/> lines to NLog. The only place the logging vendor is named.</summary>
internal sealed class NLogSink : ILogSink
{
    public void Write(Kanal.Core.Diagnostics.LogLevel level, string category, string message, Exception? error)
    {
        var logger = LogManager.GetLogger(string.IsNullOrWhiteSpace(category) ? "kanal" : category);
        logger.Log(Translate(level), error, message);
    }

    private static NLog.LogLevel Translate(Kanal.Core.Diagnostics.LogLevel level) => level switch
    {
        Kanal.Core.Diagnostics.LogLevel.Debug => NLog.LogLevel.Debug,
        Kanal.Core.Diagnostics.LogLevel.Warning => NLog.LogLevel.Warn,
        Kanal.Core.Diagnostics.LogLevel.Error => NLog.LogLevel.Error,
        _ => NLog.LogLevel.Info,
    };
}

/// <summary>
/// The host's log files: one per day, rolled over at a size the operator sets, kept for a fixed
/// number of days and never sent anywhere. A meeting cannot be replayed — whatever went wrong
/// happened once, in a room, with the other side of the table waiting — so this file is the only
/// place the answer can still be found the next morning.
/// </summary>
/// <remarks>
/// Built in code rather than from <c>NLog.config</c>: the operator changes the level in Settings
/// and it has to take effect without a restart, and a config file beside the executable is one
/// more thing that can go missing from a published build.
/// </remarks>
public static class LogSetup
{
    public const string TargetName = "kanal";

    /// <summary>Kept long enough to cover the meeting someone asks about a week later.</summary>
    public const int RetentionDays = 14;

    /// <summary>Rolled-over files from a single very loud day. Beyond this the oldest go.</summary>
    private const int MaxArchivesPerDay = 20;

    /// <summary>
    /// One line, one event: when, how bad, who wrote it, what happened — and the exception if
    /// there was one, because half an error line is not worth keeping.
    /// </summary>
    private const string LineLayout =
        "${longdate} ${level:uppercase=true:padding=-5} ${logger} ${message}" +
        "${onexception:${newline}${exception:format=tostring}}";

    public static LoggingConfiguration BuildConfiguration(string directory, AppSettings settings)
    {
        var megabytes = SettingsStore.ResolveLogMaxFileSizeMb(settings);
        var target = new FileTarget(TargetName)
        {
            // The live file keeps a stable name all day; rollovers are numbered beside it. An
            // operator asked to send "today's log" must not have to work out which of six files
            // is the current one.
            FileName = Path.Combine(directory, "kanal-${shortdate}.log"),
            ArchiveFileName = Path.Combine(directory, "kanal-${shortdate}.{#}.log"),
            ArchiveAboveSize = (long)megabytes * 1024 * 1024,
            MaxArchiveFiles = MaxArchivesPerDay,
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

    /// <summary>
    /// Points the core's log facade at the files under <see cref="SettingsStore.LogsPath"/>.
    /// Called once at startup and again whenever Settings is saved, so a level changed mid-meeting
    /// applies to the next line rather than the next launch.
    /// </summary>
    public static void Apply(AppSettings settings)
    {
        try
        {
            LogManager.Configuration = BuildConfiguration(SettingsStore.LogsPath, settings);
            Log.Install(new NLogSink());
        }
        catch
        {
            // A log folder that cannot be created is a reason to run without a log, never a
            // reason not to start: the meeting matters more than the record of it.
        }
    }

    /// <summary>Flushes and closes the files. Nothing is buffered past the host's own exit.</summary>
    public static void Shutdown()
    {
        try
        {
            LogManager.Flush();
            LogManager.Shutdown();
        }
        catch
        {
            // shutting down is not a thing worth crashing over either
        }
    }

    /// <summary>
    /// Installs the same configuration against an arbitrary folder and hands back the way out.
    /// Tests use it to assert on real files instead of on a config object; nothing in the host
    /// writes anywhere but <see cref="SettingsStore.LogsPath"/>.
    /// </summary>
    public static LogScope InstallFor(string directory, AppSettings settings)
    {
        var scope = new LogScope(Log.Sink, LogManager.Configuration);
        LogManager.Configuration = BuildConfiguration(directory, settings);
        Log.Install(new NLogSink());
        return scope;
    }

    /// <summary>What <see cref="InstallFor"/> hands back: flush now, put the old sink back later.</summary>
    public sealed class LogScope(ILogSink? previousSink, LoggingConfiguration? previousConfiguration)
        : IDisposable
    {
        public void Flush() => LogManager.Flush();

        public void Dispose()
        {
            LogManager.Flush();
            Log.Install(previousSink);
            // An empty configuration rather than null: assigning it closes the file handles this
            // scope opened — so the folder can be deleted — without shutting the factory down for
            // whatever runs next in the same process.
            LogManager.Configuration = previousConfiguration ?? new LoggingConfiguration();
        }
    }
}
