using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Kanal.Core.Diagnostics;
using Kanal.Core.Providers;
using Kanal.Core.Providers.Testing;
using Kanal.Core.Relay;
using Kanal.Host.Services;

namespace Kanal.UI.UnitTests;

/// <summary>
/// The claim the log feature is sold on: a meeting that went wrong leaves something behind. These
/// drive the real view model through the real paths and read back what the sink was handed —
/// without them, "rooms opening and closing leave a line" was a sentence in a changelog.
/// </summary>
public class RoomLoggingTests
{
    private sealed record Line(LogLevel Level, string Category, string Message, Exception? Error);

    private sealed class RecordingSink : ILogSink
    {
        private readonly List<Line> _lines = [];

        public IReadOnlyList<Line> Lines
        {
            get
            {
                lock (_lines)
                    return _lines.ToArray();
            }
        }

        public void Write(LogLevel level, string category, string message, Exception? error)
        {
            lock (_lines) // capture and dispatcher threads both write here
                _lines.Add(new Line(level, category, message, error));
        }
    }

    /// <summary>Installs a sink for the duration and puts back whatever was there before.</summary>
    private static IDisposable Listening(out RecordingSink sink)
    {
        var previous = Log.Sink;
        var recording = new RecordingSink();
        sink = recording;
        Log.Install(recording);
        return new Restore(() => Log.Install(previous));
    }

    private sealed class Restore(Action undo) : IDisposable
    {
        public void Dispose() => undo();
    }

    private static async Task PumpAsync(int ms)
    {
        var deadline = Environment.TickCount64 + ms;
        while (Environment.TickCount64 < deadline)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(20);
        }

        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public async Task ARoomOpeningAndClosingLeavesALineEach()
    {
        using var _ = Listening(out var sink);
        var vm = TestViewModels.Demo();

        await vm.StartCommand.ExecuteAsync(null);
        await PumpAsync(100);
        await vm.StopCommand.ExecuteAsync(null);

        var room = sink.Lines.Where(l => l.Category == "room").ToList();
        var opened = Assert.Single(room, l => l.Message.Contains("open:"));
        Assert.Equal(LogLevel.Info, opened.Level);
        // the mode and the languages, which is what makes an old log readable at all
        Assert.Contains("demo", opened.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("zh", opened.Message);
        Assert.Contains(room, l => l.Message.Contains("Room closed."));
    }

    /// <summary>
    /// A Start that never opens a room is the one the operator phones about, and the status line
    /// carrying the reason is gone by the time they do — so the line has to carry the reason too,
    /// not just the name of the mode that refused.
    /// </summary>
    [AvaloniaFact]
    public async Task AStartTheModeCannotServeIsLoggedWithTheReason()
    {
        using var _ = Listening(out var sink);
        // cloud transcription with no stored key: the planner refuses before anything opens
        var vm = TestViewModels.Hermetic();
        vm.SelectedMode = vm.Modes.First(o => o.Mode.Id == PipelineModeId.CloudCloud);

        await vm.StartCommand.ExecuteAsync(null);

        var refused = Assert.Single(sink.Lines, l => l.Message.Contains("Start refused"));
        Assert.Equal(LogLevel.Warning, refused.Level);
        Assert.Equal("room", refused.Category);
        Assert.Contains(vm.SelectedMode.Unavailable!, refused.Message);
    }

    /// <summary>
    /// The ending nobody chose. A transcription service that closes the socket ends the session
    /// without raising an error first, so a room that went deaf at minute 40 has to be
    /// distinguishable in the file from one the operator stopped at minute 90.
    /// </summary>
    [AvaloniaFact]
    public async Task ASessionThatEndsOnItsOwnSaysSo()
    {
        using var _ = Listening(out var sink);
        var vm = TestViewModels.Demo();
        // one short line, then the script runs out and the session ends by itself
        vm.PlanFilter = plan => plan with
        {
            Asr = new FakeAsrProvider(
                script: [new FakeAsrProvider.Line("S01", "zh", "好")],
                partialInterval: TimeSpan.FromMilliseconds(10),
                loop: false),
        };

        await vm.StartCommand.ExecuteAsync(null);
        await PumpAsync(1000);

        var ended = Assert.Single(sink.Lines, l => l.Message.StartsWith("Session ended"));
        Assert.Equal(LogLevel.Info, ended.Level);
        Assert.Contains("script finished", ended.Message);

        await vm.StopCommand.ExecuteAsync(null);
    }

    /// <summary>
    /// Losing the transcript at the last step is the worst moment for a failure that only ever
    /// appeared in a status line.
    /// </summary>
    [AvaloniaFact]
    public async Task AnExportThatCannotBeWrittenIsLoggedWithItsCause()
    {
        using var _ = Listening(out var sink);
        var vm = TestViewModels.Demo();
        await vm.StartCommand.ExecuteAsync(null);
        await PumpAsync(100);

        // a path that cannot be created on any platform this runs on
        vm.ChooseExportPath = (_, _) => Task.FromResult<string?>(
            Path.Combine(Path.GetTempPath(), "kanal-not-a-dir.txt", "nested", "room.md"));
        File.WriteAllText(Path.Combine(Path.GetTempPath(), "kanal-not-a-dir.txt"), "not a directory");

        await vm.ExportMarkdownCommand.ExecuteAsync(null);

        var failed = Assert.Single(sink.Lines, l => l.Message.Contains("transcript could not be written"));
        Assert.Equal(LogLevel.Error, failed.Level);
        Assert.NotNull(failed.Error);

        await vm.StopCommand.ExecuteAsync(null);
    }

    /// <summary>
    /// Debug is offered in Settings, so it has to mean something. Before this it was byte-identical
    /// to Info: nothing in the host wrote a single Debug line, and an operator told to "turn on
    /// Debug and reproduce it" sent back the same file.
    /// </summary>
    [AvaloniaFact]
    public async Task TheDebugLevelCarriesSomethingInfoDoesNot()
    {
        using var _ = Listening(out var sink);
        var vm = TestViewModels.Demo();
        vm.RelayEnabled = true;
        vm.RelayPublisherFactory = _ => new NullRelayPublisher();

        await vm.StartCommand.ExecuteAsync(null);
        await PumpAsync(100);
        await vm.StopCommand.ExecuteAsync(null);

        Assert.NotEmpty(sink.Lines.Where(l => l.Level == LogLevel.Debug));
    }

    /// <summary>
    /// A teardown that throws. Stop flushes and rewrites the WAV header, which fails on a full
    /// disk — and the "Room closed" line was the last statement in the try, so the operator got a
    /// corrupt recording, a faulted task, and a file showing a room that opened and never closed:
    /// indistinguishable from a crash, which is the silence this feature exists to end.
    /// </summary>
    [AvaloniaFact]
    public async Task AStopThatThrowsMidTeardownStillRecordsThatTheRoomEnded()
    {
        using var _ = Listening(out var sink);
        var vm = TestViewModels.Demo();
        vm.PlanFilter = plan => plan with { Asr = new ThrowingTeardownAsr(plan.Asr!) };

        await vm.StartCommand.ExecuteAsync(null);
        await PumpAsync(100);
        await vm.StopCommand.ExecuteAsync(null);

        var room = sink.Lines.Where(l => l.Category == "room").ToList();
        var failed = Assert.Single(room, l => l.Level == LogLevel.Error);
        Assert.Contains("not enough space", failed.Error?.Message ?? "");
        Assert.Contains(room, l => l.Message.Contains("Room closed."));
        // and the buttons come back, as they did before
        Assert.True(vm.StartCommand.CanExecute(null));
    }

    /// <summary>Stands in for the recorder failing to rewrite the WAV header on a full disk.</summary>
    private sealed class ThrowingTeardownAsr(IAsrProvider inner) : IAsrProvider, IAsyncDisposable
    {
        public string Id => inner.Id;

        public AsrCapabilities Caps => inner.Caps;

        public Task<IAsrSession> StartAsync(AsrSessionOptions options, CancellationToken ct) =>
            inner.StartAsync(options, ct);

        public ValueTask DisposeAsync() =>
            throw new IOException("There is not enough space on the disk.");
    }

    /// <summary>
    /// The cloud path's way in. A provider that rejects a request and echoes the text it rejected
    /// puts what was said in the room into its own error message, and that message used to be
    /// written at Error or Warning — on disk, in a file the operator is told to send on. Only the
    /// level the operator turns on deliberately carries a provider's own words now.
    /// </summary>
    [AvaloniaFact]
    public async Task AProviderErrorEchoingWhatWasSaidIsOnlyWrittenAtDebug()
    {
        const string spoken = "这批支架的料号是 KX-4402";
        using var _ = Listening(out var sink);
        var vm = TestViewModels.Demo();
        vm.PlanFilter = plan => plan with
        {
            Asr = new ErroringAsr($"400 rejected: {{\"text\":\"{spoken}\"}}"),
        };

        await vm.StartCommand.ExecuteAsync(null);
        await PumpAsync(400);
        await vm.StopCommand.ExecuteAsync(null);

        var carrying = sink.Lines
            .Where(l => $"{l.Message} {l.Error}".Contains(spoken))
            .ToList();
        // the detail is still reachable…
        Assert.True(carrying.Count > 0, "the provider's text was dropped rather than moved to Debug");
        Assert.All(carrying, l => Assert.Equal(LogLevel.Debug, l.Level)); // …and only there

        // the failure itself is still on the record at the default level
        Assert.Contains(sink.Lines, l => l.Level == LogLevel.Warning && l.Category == "room");
    }

    /// <summary>An ASR provider whose only event is an error carrying the caller's own text.</summary>
    private sealed class ErroringAsr(string message) : IAsrProvider
    {
        public string Id => "erroring";

        public AsrCapabilities Caps { get; } = new(
            Streaming: true,
            Diarization: false,
            Translation: false,
            AutoLanguageDetect: false,
            Languages: new HashSet<string> { "zh", "de", "pl", "en" },
            Latency: LatencyClass.Realtime);

        public Task<IAsrSession> StartAsync(AsrSessionOptions options, CancellationToken ct) =>
            Task.FromResult<IAsrSession>(new Session(message));

        private sealed class Session(string message) : IAsrSession
        {
            public IAsyncEnumerable<AsrEvent> Events => Emit();

            public ValueTask PushAudioAsync(ReadOnlyMemory<byte> pcm16, CancellationToken ct = default) =>
                ValueTask.CompletedTask;

            private async IAsyncEnumerable<AsrEvent> Emit()
            {
                await Task.Yield();
                yield return new AsrEvent.Error(message, Fatal: false);
            }

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    /// <summary>Nothing a participant said, and no credential, may end up in the file.</summary>
    [AvaloniaFact]
    public async Task NothingSaidInTheRoomIsWrittenToTheLog()
    {
        using var _ = Listening(out var sink);
        var settings = new AppSettings { ApiKeys = { new ApiKeyEntry("prod", "gladia", "sk-secret-key") } };
        var vm = TestViewModels.Demo(settings);

        await vm.StartCommand.ExecuteAsync(null);
        await PumpAsync(400); // long enough for the scripted demo to produce utterances
        await vm.StopCommand.ExecuteAsync(null);

        // whatever the demo script said is on screen; none of it is in the log
        var spoken = vm.Columns.SelectMany(c => c.Bubbles).Select(b => b.Text)
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .ToList();
        Assert.NotEmpty(spoken);

        var written = string.Join("\n", sink.Lines.Select(l => $"{l.Message} {l.Error}"));
        foreach (var line in spoken)
            Assert.DoesNotContain(line, written);
        Assert.DoesNotContain("sk-secret-key", written);
    }
}
