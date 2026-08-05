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

        // our own words on the record: the operator did not stop this one…
        var ended = Assert.Single(sink.Lines, l => l.Message.Contains("ended on its own"));
        Assert.Equal(LogLevel.Info, ended.Level);

        // …and the provider's reason beside it, at the level that carries verbatim text
        var reason = Assert.Single(sink.Lines, l => l.Message.Contains("script finished"));
        Assert.Equal(LogLevel.Debug, reason.Level);

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
        // the machine's own words survive classification: an operating system says "there is not
        // enough space on the disk", never a response body, and that is the line worth reading
        Assert.Contains("not enough space", failed.Message);
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

    /// <summary>Every door a provider's or the gateway's own words can come through.</summary>
    public enum Verbatim
    {
        /// <summary>The transcriber reports an error mid-meeting.</summary>
        SessionError,

        /// <summary>The transcriber ends the stream and names a reason.</summary>
        SessionEnded,

        /// <summary>The transcriber refuses to open a session at all.</summary>
        StartFailure,

        /// <summary>The gateway refuses a publish — and the publish is the meeting.</summary>
        RelayPublish,
    }

    /// <summary>
    /// The guarantee, as a property of the log rather than of one call site. A provider or a
    /// gateway that rejects a request quotes the request back, and the request is what was said in
    /// the room — so no path may write that string at a level the operator has not opted into.
    /// Per-site tests let the next site reopen this quietly; this one fails instead.
    /// </summary>
    [AvaloniaTheory]
    [InlineData(Verbatim.SessionError)]
    [InlineData(Verbatim.SessionEnded)]
    [InlineData(Verbatim.StartFailure)]
    [InlineData(Verbatim.RelayPublish)]
    public async Task NoPathWritesAProvidersOwnWordsAboveDebug(Verbatim path)
    {
        // shaped like what a rejected request carries back: a part number and a delivery date
        const string spoken = "这批支架的料号是 KX-4402，8月29日前发货";
        var echoed = $"400 rejected: {{\"text\":\"{spoken}\"}}";

        using var _ = Listening(out var sink);
        var vm = TestViewModels.Demo();
        switch (path)
        {
            case Verbatim.SessionError:
                vm.PlanFilter = plan => plan with { Asr = FaultingAsr.Erroring(echoed) };
                break;
            case Verbatim.SessionEnded:
                vm.PlanFilter = plan => plan with { Asr = FaultingAsr.Ending(echoed) };
                break;
            case Verbatim.StartFailure:
                vm.PlanFilter = plan => plan with { Asr = FaultingAsr.RefusingToStart(echoed) };
                break;
            case Verbatim.RelayPublish:
                vm.RelayEnabled = true;
                vm.RelayPublisherFactory = _ => new RefusingRelayPublisher(echoed);
                break;
        }

        await vm.StartCommand.ExecuteAsync(null);
        await PumpAsync(400);
        await vm.StopCommand.ExecuteAsync(null);

        var carrying = sink.Lines.Where(l => $"{l.Message} {l.Error}".Contains(spoken)).ToList();
        var loud = carrying.Where(l => l.Level != LogLevel.Debug).ToList();
        Assert.True(
            loud.Count == 0,
            $"{path} put what was said in the room on disk at {string.Join(", ", loud.Select(l => l.Level))}: " +
            $"{loud.FirstOrDefault()?.Message}");

        // …and it is still reachable where the operator asked for it
        Assert.True(carrying.Count > 0, $"{path} dropped the detail rather than moving it to Debug");

        // …and what happened is still on the record at the default level, in our own words
        var ours = path switch
        {
            Verbatim.SessionError => "reported an error",
            Verbatim.SessionEnded => "ended on its own",
            Verbatim.StartFailure => "failed to start",
            _ => "did not publish",
        };
        Assert.Contains(
            sink.Lines,
            l => l.Level != LogLevel.Debug && l.Message.Contains(ours));
    }

    /// <summary>A gateway that refuses everything, quoting back the payload it refused.</summary>
    private sealed class RefusingRelayPublisher(string echoed) : IRelayPublisher
    {
        private int _published;

        // The first publish is the room config, inside StartAsync. Letting that one through means
        // the room actually opens, so the snapshot, room-closed and generic publish paths are
        // exercised too instead of the run stopping at the start-failure line.
        public Task PublishAsync(RelayMessage message, CancellationToken ct = default) =>
            Interlocked.Increment(ref _published) == 1
                ? Task.CompletedTask
                : throw new RelayPublishException(403, $"Relay publish failed (403): {echoed}");

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    /// <summary>
    /// An ASR provider that fails in one of the three ways a real one does, each carrying the text
    /// it was handed straight back.
    /// </summary>
    private sealed class FaultingAsr(string? message, AsrEvent? single, bool throwOnStart) : IAsrProvider
    {
        public static FaultingAsr Erroring(string message) =>
            new(message, new AsrEvent.Error(message, Fatal: false), throwOnStart: false);

        public static FaultingAsr Ending(string message) =>
            new(message, new AsrEvent.Ended(message), throwOnStart: false);

        public static FaultingAsr RefusingToStart(string message) =>
            new(message, null, throwOnStart: true);

        public string Id => "faulting";

        public AsrCapabilities Caps { get; } = new(
            Streaming: true,
            Diarization: false,
            Translation: false,
            AutoLanguageDetect: false,
            Languages: new HashSet<string> { "zh", "de", "pl", "en" },
            Latency: LatencyClass.Realtime);

        public Task<IAsrSession> StartAsync(AsrSessionOptions options, CancellationToken ct) =>
            throwOnStart
                ? throw new InvalidOperationException(message)
                : Task.FromResult<IAsrSession>(new Session(single!));

        private sealed class Session(AsrEvent single) : IAsrSession
        {
            public IAsyncEnumerable<AsrEvent> Events => Emit();

            public ValueTask PushAudioAsync(ReadOnlyMemory<byte> pcm16, CancellationToken ct = default) =>
                ValueTask.CompletedTask;

            private async IAsyncEnumerable<AsrEvent> Emit()
            {
                await Task.Yield();
                yield return single;
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
