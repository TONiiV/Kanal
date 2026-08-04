using System;

namespace Kanal.Core.Diagnostics;

/// <summary>How much of what happened is worth keeping. Ordered quietest-last.</summary>
public enum LogLevel
{
    /// <summary>Frame counts, message payloads, timings — only useful when reproducing a fault.</summary>
    Debug = 0,

    /// <summary>The shape of the meeting: rooms opened and closed, providers chosen, files written.</summary>
    Info = 1,

    /// <summary>Something failed but the meeting carried on — a snapshot that did not publish.</summary>
    Warning = 2,

    /// <summary>Something failed and stopped working — a session that could not start.</summary>
    Error = 3,
}

/// <summary>
/// Where a log line goes. Implemented in the host over NLog; the core never names a logging
/// vendor, for the same reason it never names an ASR one.
/// </summary>
public interface ILogSink
{
    void Write(LogLevel level, string category, string message, Exception? error);
}

/// <summary>
/// The one call site for "write this down". Nothing is written until a host installs a sink, so
/// tests, tools and the doctor stay silent by default and no library decides on its own where a
/// file lands on someone's disk.
/// </summary>
/// <remarks>
/// A static rather than an injected logger: every layer logs, including static helpers and
/// capture loops that have no constructor to thread one through, and a meeting-long room has no
/// container to resolve it from. The cost — a global — is bounded by the interface being one
/// method and by <see cref="Install"/> being the only way to set it.
/// </remarks>
public static class Log
{
    /// <summary>
    /// Volatile: installed from the UI thread, read from capture, COM and dispatcher threads.
    /// Nothing in the memory model otherwise requires a logging thread to notice a sink that was
    /// installed after it started.
    /// </summary>
    private static volatile ILogSink? _sink;

    /// <summary>The installed sink, or null while nothing is listening. Restore-friendly for tests.</summary>
    public static ILogSink? Sink => _sink;

    public static void Install(ILogSink? sink) => _sink = sink;

    public static void Debug(string category, string message) =>
        Write(LogLevel.Debug, category, message, null);

    public static void Info(string category, string message) =>
        Write(LogLevel.Info, category, message, null);

    public static void Warning(string category, string message, Exception? error = null) =>
        Write(LogLevel.Warning, category, message, error);

    public static void Error(string category, string message, Exception? error = null) =>
        Write(LogLevel.Error, category, message, error);

    /// <summary>
    /// A full disk, a locked file, a log folder deleted mid-meeting: writing a line is never the
    /// operation that takes the room down. Failures here are swallowed on purpose — there is by
    /// definition nowhere left to report them to.
    /// </summary>
    public static void Write(LogLevel level, string category, string message, Exception? error)
    {
        var sink = _sink;
        if (sink is null)
            return;

        try
        {
            sink.Write(level, category, message, error);
        }
        catch
        {
            // see above
        }
    }
}
