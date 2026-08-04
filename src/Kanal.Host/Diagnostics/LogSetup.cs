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

    /// <summary>
    /// One line, one event: when, how bad, who wrote it, what happened — and the exception if
    /// there was one, because half an error line is not worth keeping.
    /// </summary>
    private const string LineLayout =
        "${longdate} ${level:uppercase=true:padding=-5} ${logger} ${message}" +
        "${onexception:${newline}${exception:format=tostring}}";

    private static long ArchiveBytes(AppSettings settings) =>
        (long)SettingsStore.ResolveLogMaxFileSizeMb(settings) * 1024 * 1024;

    public static LoggingConfiguration BuildConfiguration(string directory, AppSettings settings)
    {
        var target = new FileTarget(TargetName)
        {
            // The live file keeps a stable name all day; rollovers are numbered beside it. An
            // operator asked to send "today's log" must not have to work out which of six files
            // is the current one.
            FileName = Path.Combine(directory, "kanal-${shortdate}.log"),
            // Same name as the live file: NLog numbers the rollovers off it (kanal-<date>_1.log)
            // and leaves the live one alone. No `{#}` — that is NLog 5's placeholder, and 6 strips
            // it rather than substituting it, so a name written with one describes files that
            // never appear.
            ArchiveFileName = Path.Combine(directory, "kanal-${shortdate}.log"),
            ArchiveAboveSize = ArchiveBytes(settings),
            // Age is the only cap. NLog's file *count* cap counts every file matching the pattern
            // across dates, not per day: set beside a day-based retention it wins, and a busy
            // meeting at the smallest rollover size cut the two weeks this panel promises down to
            // hours. The promise on screen is the one that has to hold.
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
    public static void Apply(AppSettings settings) => ApplyTo(SettingsStore.LogsPath, settings);

    /// <summary>
    /// <see cref="Apply"/> against an explicit folder. Public for the tests, which assert on real
    /// files rather than on a configuration object; nothing in the host writes anywhere but
    /// <see cref="SettingsStore.LogsPath"/>.
    /// </summary>
    /// <remarks>
    /// A second run updates the target that is already open rather than replacing it. Handing NLog
    /// a fresh <c>FileTarget</c> over the same open file cost the operator the lines already
    /// written: the new target opened at a stale offset and overwrote a buffer's worth of what the
    /// old one had flushed — thousands of lines under load, at the exact moment someone turned
    /// Debug on because something was going wrong.
    /// </remarks>
    public static void ApplyTo(string directory, AppSettings settings)
    {
        try
        {
            var expected = Path.Combine(directory, "kanal-${shortdate}.log");
            if (LogManager.Configuration is { } live &&
                live.FindTargetByName(TargetName) is FileTarget target &&
                target.FileName.ToString() == expected)
            {
                target.ArchiveAboveSize = ArchiveBytes(settings);
                foreach (var rule in live.LoggingRules)
                    rule.SetLoggingLevels(Floor(settings.LogLevel), NLog.LogLevel.Fatal);
                LogManager.ReconfigExistingLoggers();
            }
            else
            {
                LogManager.Configuration = BuildConfiguration(directory, settings);
            }

            Log.Install(new NLogSink());
        }
        catch
        {
            // A log folder that cannot be created is a reason to run without a log, never a
            // reason not to start: the meeting matters more than the record of it.
        }
    }

    /// <summary>
    /// Pushes anything still buffered to disk. Deliberately not a shutdown: the host flushes on
    /// its way out, but a capture loop or a session still unwinding writes after that — and those
    /// lines are the ones that explain why it is on its way out. Closing NLog there swallowed them.
    /// </summary>
    public static void Flush()
    {
        try
        {
            LogManager.Flush();
        }
        catch
        {
            // flushing is not a thing worth crashing over either
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
