using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using System.Net.Http;
using System.Text;
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

    /// <summary>
    /// Every door an outside string can come through, one per call site. Separate cases rather than
    /// one "relay" case: three publish paths collapsed into a single scenario meant a mutation to
    /// any one of them was covered by whichever of the other two still fired.
    /// </summary>
    public enum Verbatim
    {
        /// <summary>The transcriber reports an error mid-meeting.</summary>
        SessionError,

        /// <summary>The transcriber ends the stream and names a reason.</summary>
        SessionEnded,

        /// <summary>The transcriber refuses to open a session at all.</summary>
        StartFailure,

        /// <summary>The gateway refuses to create the room, so there is no QR code.</summary>
        RelaySetup,

        /// <summary>The gateway refuses the closing snapshot — which is every utterance in it.</summary>
        RelaySnapshot,

        /// <summary>The gateway refuses the room-closed message.</summary>
        RelayClosed,

        /// <summary>
        /// The gateway refuses the room-moved message on a second Start. Its payload is the new
        /// room's verification key and invite ticket, so what leaks here is a join credential.
        /// </summary>
        RelayMoved,
    }

    /// <summary>
    /// Which line each site writes. Pinning both halves to wording only that site produces is what
    /// makes a mutation red the case that covers it and no other: assertions matched on any
    /// non-Debug line, or any Debug line, were satisfied by a neighbouring site instead.
    /// </summary>
    private static (string OnTheRecord, string AtDebug) Wording(Verbatim path) => path switch
    {
        Verbatim.SessionError => ("The session reported an error", "Session error:"),
        Verbatim.SessionEnded => ("The session ended on its own", "Session end reason:"),
        Verbatim.StartFailure => ("failed to start", "The failure that stopped the start:"),
        Verbatim.RelaySetup => ("The relay could not be set up", "The relay setup failure:"),
        Verbatim.RelaySnapshot => ("The closing snapshot did not publish", "The snapshot publish failure:"),
        Verbatim.RelayClosed => ("The room-closed message did not publish", "The room-closed publish failure:"),
        Verbatim.RelayMoved => ("A RoomMovedMessage did not publish", "The publish failure:"),
        _ => throw new ArgumentOutOfRangeException(nameof(path)),
    };

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
    [InlineData(Verbatim.RelaySetup)]
    [InlineData(Verbatim.RelaySnapshot)]
    [InlineData(Verbatim.RelayClosed)]
    [InlineData(Verbatim.RelayMoved)]
    public async Task NoPathWritesAProvidersOwnWordsAboveDebug(Verbatim path)
    {
        // shaped like what a rejected request carries back: a part number and a delivery date
        const string spoken = "这批支架的料号是 KX-4402，8月29日前发货";
        var echoed = $"400 rejected: {{\"text\":\"{spoken}\"}}";
        var refusal = new RelayPublishException(403, $"Relay publish failed (403): {echoed}");

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
            case Verbatim.RelaySetup:
                // A factory that throws stands in for CreateRoomAsync being refused: the same
                // exception reaches the same catch, without a socket.
                vm.RelayEnabled = true;
                vm.RelayPublisherFactory = _ => throw new RelayPublishException(
                    403, $"Relay room creation failed (403): {echoed}");
                break;
            default:
                // One message kind each, so exactly one publish site runs per case.
                vm.RelayEnabled = true;
                vm.RelayPublisherFactory = _ => new SelectivelyRefusingRelayPublisher(
                    path switch
                    {
                        Verbatim.RelaySnapshot => typeof(RoomSnapshotMessage),
                        Verbatim.RelayClosed => typeof(RoomClosedMessage),
                        _ => typeof(RoomMovedMessage),
                    },
                    refusal);
                break;
        }

        await vm.StartCommand.ExecuteAsync(null);
        await PumpAsync(400);
        await vm.StopCommand.ExecuteAsync(null);

        if (path == Verbatim.RelayMoved)
        {
            // The room-moved message is only published when a room is already open — the second
            // Start telling the phones on the old channel where the meeting went. One Start never
            // reaches that line at all, which is how this site went uncovered.
            await vm.StartCommand.ExecuteAsync(null);
            await PumpAsync(200);
            await vm.StopCommand.ExecuteAsync(null);
        }

        var (onTheRecord, atDebug) = Wording(path);
        bool Carries(Line l) => $"{l.Message} {l.Error}".Contains(spoken);

        // (a) nothing the room said, at any level the operator did not opt into
        var loud = sink.Lines.Where(l => l.Level != LogLevel.Debug && Carries(l)).ToList();
        Assert.True(
            loud.Count == 0,
            $"{path} put what was said in the room on disk at {string.Join(", ", loud.Select(l => l.Level))}: " +
            $"{loud.FirstOrDefault()?.Message}");

        // (b) still reachable at Debug — from this site's own line, not a neighbour's
        Assert.True(
            sink.Lines.Any(l => l.Level == LogLevel.Debug && l.Message.Contains(atDebug) && Carries(l)),
            $"{path} wrote no Debug line of its own (\"{atDebug}\") carrying the detail");

        // (c) and what happened is on the record at the default level, in this site's own words
        Assert.True(
            sink.Lines.Any(l => l.Level != LogLevel.Debug && l.Message.Contains(onTheRecord)),
            $"{path} left nothing on the record saying \"{onTheRecord}\"");
    }

    /// <summary>
    /// Also on screen. The relay warning is printed beside the QR code the participants are looking
    /// at, and it was the gateway's response body verbatim.
    /// </summary>
    [AvaloniaFact]
    public async Task ARefusedRelayDoesNotPutTheGatewaysWordsBesideTheQrCode()
    {
        const string spoken = "这批支架的料号是 KX-4402";
        using var _ = Listening(out var _sink);
        var vm = TestViewModels.Demo();
        vm.RelayEnabled = true;
        vm.RelayPublisherFactory = _ => throw new RelayPublishException(
            403, $"Relay room creation failed (403): {spoken}");

        await vm.StartCommand.ExecuteAsync(null);
        await PumpAsync(100);

        Assert.DoesNotContain(spoken, vm.JoinError);
        Assert.DoesNotContain(spoken, vm.Status);
        Assert.Contains("credential", vm.JoinError); // the classification, in our own words

        await vm.StopCommand.ExecuteAsync(null);
    }

    /// <summary>
    /// The carve-out that keeps an operating system's own words on the record is by exact type, not
    /// by assignability. <c>HttpIOException</c> derives from <c>IOException</c> and is written by
    /// whatever answered the request — a body read that fails halfway through carries whatever
    /// arrived, which is the payload again.
    /// </summary>
    [AvaloniaFact]
    public async Task AnHttpFailureIsNotMistakenForTheMachinesOwnWords()
    {
        const string spoken = "这批支架的料号是 KX-4402";
        using var _ = Listening(out var sink);
        var vm = TestViewModels.Demo();
        vm.RelayEnabled = true;
        vm.RelayPublisherFactory = _ => throw new HttpIOException(
            HttpRequestError.ResponseEnded, $"The response ended prematurely: {spoken}");

        await vm.StartCommand.ExecuteAsync(null);
        await PumpAsync(100);
        await vm.StopCommand.ExecuteAsync(null);

        var loud = sink.Lines
            .Where(l => l.Level != LogLevel.Debug && $"{l.Message} {l.Error}".Contains(spoken))
            .ToList();
        Assert.True(loud.Count == 0, $"an HttpIOException reached the record: {loud.FirstOrDefault()?.Message}");
    }

    /// <summary>Refuses one kind of message and passes the rest, so one site runs per case.</summary>
    private sealed class SelectivelyRefusingRelayPublisher(Type refused, Exception refusal) : IRelayPublisher
    {
        public Task PublishAsync(RelayMessage message, CancellationToken ct = default) =>
            Unwrap(message).GetType() == refused ? throw refusal : Task.CompletedTask;

        // What reaches a publisher is the signed envelope; the kind being refused is inside it.
        private static RelayMessage Unwrap(RelayMessage message)
        {
            if (message is not SignedRelayMessage signed)
                return message;

            var encoded = signed.Data.Replace('-', '+').Replace('_', '/');
            encoded = encoded.PadRight(encoded.Length + ((4 - encoded.Length % 4) % 4), '=');
            return RelayJson.Deserialize(Encoding.UTF8.GetString(Convert.FromBase64String(encoded)))
                ?? throw new InvalidOperationException("Signed test message had no payload.");
        }

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
