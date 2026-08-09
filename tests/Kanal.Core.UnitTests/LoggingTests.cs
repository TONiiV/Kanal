using Kanal.Core.Diagnostics;

namespace Kanal.Core.UnitTests;

[CollectionDefinition(LoggingCollection.Name, DisableParallelization = true)]
public sealed class LoggingCollection
{
    public const string Name = "logging";
}

[Collection(LoggingCollection.Name)]
public class LoggingTests
{
    private sealed record Line(LogLevel Level, string Category, string Message, Exception? Error);

    private sealed class RecordingSink : ILogSink
    {
        public List<Line> Lines { get; } = new();

        public void Write(LogLevel level, string category, string message, Exception? error) =>
            Lines.Add(new Line(level, category, message, error));
    }

    private sealed class ThrowingSink : ILogSink
    {
        public void Write(LogLevel level, string category, string message, Exception? error) =>
            throw new IOException("the log volume is full");
    }

    private static IDisposable Installed(ILogSink? sink)
    {
        var previous = Log.Sink;
        Log.Install(sink);
        return new Restore(() => Log.Install(previous));
    }

    private sealed class Restore(Action undo) : IDisposable
    {
        public void Dispose() => undo();
    }

    [Fact]
    public void WithNoSinkInstalledLoggingIsASilentNoOp()
    {
        using var _ = Installed(null);

        Log.Debug("test", "nothing is listening");
        Log.Info("test", "nothing is listening");
        Log.Warning("test", "nothing is listening");
        Log.Error("test", "nothing is listening", new InvalidOperationException("boom"));
    }

    [Fact]
    public void EachLevelReachesTheSinkWithItsCategoryAndMessage()
    {
        var sink = new RecordingSink();
        using var _ = Installed(sink);

        Log.Debug("audio", "frame 1");
        Log.Info("room", "started");
        Log.Warning("relay", "publish retried");
        var error = new InvalidOperationException("no key");
        Log.Error("asr", "session failed", error);

        Assert.Equal(
            [LogLevel.Debug, LogLevel.Info, LogLevel.Warning, LogLevel.Error],
            sink.Lines.Select(l => l.Level));
        Assert.Equal(["audio", "room", "relay", "asr"], sink.Lines.Select(l => l.Category));
        Assert.Equal("started", sink.Lines[1].Message);
        Assert.Same(error, sink.Lines[3].Error);
        Assert.All(sink.Lines.Take(3), l => Assert.Null(l.Error));
    }

    [Fact]
    public void ASinkThatThrowsNeverReachesTheCaller()
    {
        using var _ = Installed(new ThrowingSink());

        Log.Info("room", "started");
        Log.Error("room", "stopped", new Exception("x"));
    }

    [Fact]
    public void TheLevelsAreOrderedBySeverity()
    {
        Assert.True(LogLevel.Debug < LogLevel.Info);
        Assert.True(LogLevel.Info < LogLevel.Warning);
        Assert.True(LogLevel.Warning < LogLevel.Error);
    }
}
