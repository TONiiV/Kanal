using System;

namespace Kanal.Core.Diagnostics;

public enum LogLevel
{
    Debug = 0,

    Info = 1,

    Warning = 2,

    Error = 3,
}

public interface ILogSink
{
    void Write(LogLevel level, string category, string message, Exception? error);
}

public static class Log
{
    // volatile: installed from the UI thread, read from capture and dispatcher threads.
    private static volatile ILogSink? _sink;

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
            // Writing a line is never the operation that takes the room down.
        }
    }
}
