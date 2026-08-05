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
    /// <summary>
    /// Ceiling on one line's exception text. Providers and the gateway put whole HTTP response
    /// bodies into exception messages — a captive portal's 20 KB error page, verbatim, per
    /// failure — and the snapshot timer retries every 15 seconds, so an afternoon of one repeated
    /// failure filled the file on its own. A stack trace fits comfortably under this.
    /// </summary>
    private const int MaxExceptionChars = 4000;

    /// <summary>And on the message, which can carry a provider's text the same way.</summary>
    private const int MaxMessageChars = 2000;

    public void Write(Kanal.Core.Diagnostics.LogLevel level, string category, string message, Exception? error)
    {
        var logger = LogManager.GetLogger(string.IsNullOrWhiteSpace(category) ? "kanal" : category);
        var line = Cap(message, MaxMessageChars);
        if (error is not null)
            line += Environment.NewLine + Cap(error.ToString(), MaxExceptionChars);

        // Rendered here rather than handed to NLog as an exception: `${exception:format=tostring}`
        // has no ceiling, which is the whole problem.
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
    /// <remarks>
    /// No <c>${exception}</c>: the sink renders and caps it into the message, because that
    /// renderer has no length ceiling and the text it prints is not ours.
    /// </remarks>
    private const string LineLayout =
        "${longdate} ${level:uppercase=true:padding=-5} ${logger} ${message}";

    private static long ArchiveBytes(AppSettings settings) =>
        (long)SettingsStore.ResolveLogMaxFileSizeMb(settings) * 1024 * 1024;

    /// <summary>
    /// How much disk the archive count aims to bound the folder to. A target, not a ceiling: past
    /// roughly 102 MB per file it can no longer be met without dropping below
    /// <see cref="MinArchives"/>, and retention wins that argument. What is actually guaranteed is
    /// <c>MaxArchiveFiles × size ≤ max(DiskBudgetMb, MinArchives × size)</c> — 2 GB at the default,
    /// 20 GB at the largest rollover the panel offers, which is a size only a deliberate choice
    /// reaches.
    /// </summary>
    public const int DiskBudgetMb = 2048;

    /// <summary>Never fewer than this many rollovers, however large the operator made them.</summary>
    public const int MinArchives = 20;

    /// <summary>
    /// A count that bounds the folder without standing in for the day-based retention: at the
    /// default 10 MB it is 204 files, which a fortnight of ordinary meetings will not approach, so
    /// in practice age is what deletes. It bites only when something is writing in a loop, which is
    /// exactly when a bound is wanted.
    /// </summary>
    private static int ArchiveCountBackstop(AppSettings settings) =>
        Math.Max(MinArchives, DiskBudgetMb / SettingsStore.ResolveLogMaxFileSizeMb(settings));

    /// <summary>
    /// The most the folder can hold before something is deleted: the archives the backstop allows,
    /// plus the file being written. Public because the operator picks the size that determines it
    /// and has no other way to find out what they picked — at the largest rollover the panel offers
    /// this is twenty gigabytes, which is a number worth seeing before it is on the disk.
    /// </summary>
    public static int MaxFolderMb(AppSettings settings) =>
        (ArchiveCountBackstop(settings) + 1) * SettingsStore.ResolveLogMaxFileSizeMb(settings);

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
            // Age is the retention policy; the count is only a runaway backstop. NLog's file-count
            // cap counts every file matching the pattern across dates, so a small fixed number
            // silently undercuts the two weeks this panel promises — 20 archives at the smallest
            // rollover size cut it to hours. Dropping it altogether was worse: one loud day at 1 MB
            // wrote 63 files and 65 MB with nothing eligible for deletion yet. So: derived from the
            // size, sized to a disk budget no ordinary fortnight will reach.
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
        // Installed first and outside the try: a failure below leaves the sink in place rather
        // than leaving the process permanently unable to log anything at all.
        Log.Install(new NLogSink());

        try
        {
            // Eagerly, so an unwritable folder fails here where it is caught and can be reported,
            // rather than inside NLog — which defers file creation and swallows the failure, so
            // every line afterwards vanished with the panel still promising a file a day.
            Directory.CreateDirectory(directory);

            var expected = Path.Combine(directory, "kanal-${shortdate}.log");
            if (LogManager.Configuration is { } live &&
                live.FindTargetByName(TargetName) is FileTarget target &&
                target.FileName.ToString() == expected)
            {
                target.ArchiveAboveSize = ArchiveBytes(settings);
                // With the size and without it, or the backstop stays derived from whatever size
                // the previous apply saw. Program.Main applies the defaults and then the stored
                // settings, so that is not a mid-meeting edge case — it is every launch, and at
                // 1 MB it left 204 archives standing in for the 2048 the budget intends.
                target.MaxArchiveFiles = ArchiveCountBackstop(settings);
                foreach (var rule in live.LoggingRules)
                    rule.SetLoggingLevels(Floor(settings.LogLevel), NLog.LogLevel.Fatal);
                LogManager.ReconfigExistingLoggers();
            }
            else
            {
                LogManager.Configuration = BuildConfiguration(directory, settings);
            }

            Writable = true;
            // Cleared with it: a reason left standing beside a folder that now opens is a
            // contradiction the next reader of either one has to untangle.
            FailureReason = null;
        }
        catch (Exception ex)
        {
            // A log folder that cannot be created is a reason to run without a log, never a
            // reason not to start: the meeting matters more than the record of it. But the panel
            // must stop promising a file that is not being written, which is what Writable is for.
            Writable = false;
            FailureReason = ex.Message;
        }
    }

    /// <summary>
    /// Whether the last <see cref="ApplyTo"/> managed to open a folder to write into. False means
    /// every line since is gone — worth saying on the screen that offers to open that folder.
    /// </summary>
    public static bool Writable { get; private set; }

    /// <summary>Why not, when <see cref="Writable"/> is false.</summary>
    public static string? FailureReason { get; private set; }

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
