using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Kanal.Core.Models;
using Kanal.Core.Providers;
using Kanal.Core.Providers.Testing;
using Kanal.Core.Relay;
using Kanal.Core.Room;

namespace Kanal.Tests;

public class MeetingSessionTests
{
    private sealed class RecordingRelay : IRelayPublisher
    {
        public List<RelayMessage> Messages { get; } = new();

        public Task PublishAsync(RelayMessage message, CancellationToken ct = default)
        {
            lock (Messages)
            {
                Messages.Add(message);
            }

            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        public List<T> OfType<T>()
        {
            lock (Messages)
            {
                return Messages.OfType<T>().ToList();
            }
        }
    }

    private sealed class CountingMt : IMtProvider
    {
        public int Calls;

        public string Id => "counting";

        public Task<IReadOnlyDictionary<string, string>> TranslateAsync(
            string text, string from, IReadOnlyList<string> to,
            IReadOnlyList<Utterance> context, CancellationToken ct)
        {
            Interlocked.Increment(ref Calls);
            return Task.FromResult<IReadOnlyDictionary<string, string>>(
                to.ToDictionary(l => l, l => $"{l}:{text}"));
        }
    }

    /// <summary>Stands in for a local model mid-decode: returns only when cancelled.</summary>
    private sealed class BlockingMt : IMtProvider
    {
        private readonly TaskCompletionSource _started =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Started => _started.Task;
        public bool Cancelled;

        public string Id => "blocking";

        public async Task<IReadOnlyDictionary<string, string>> TranslateAsync(
            string text, string from, IReadOnlyList<string> to,
            IReadOnlyList<Utterance> context, CancellationToken ct)
        {
            _started.TrySetResult();
            try
            {
                await Task.Delay(Timeout.Infinite, ct);
            }
            catch (OperationCanceledException)
            {
                Cancelled = true;
                throw;
            }

            return new Dictionary<string, string>();
        }
    }

    /// <summary>Slow enough that shutdown always finds it in flight, quick enough to fit a grace.</summary>
    private sealed class SlowMt : IMtProvider
    {
        public string Id => "slow";

        public async Task<IReadOnlyDictionary<string, string>> TranslateAsync(
            string text, string from, IReadOnlyList<string> to,
            IReadOnlyList<Utterance> context, CancellationToken ct)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(300), ct);
            return to.ToDictionary(l => l, l => $"{l}:{text}");
        }
    }

    /// <summary>
    /// ASR whose event channel outlives its session's disposal — the real drain window: finals
    /// buffered before Stop are still handed to the pump after the session object is gone.
    /// </summary>
    private sealed class DrainingAsr : IAsrProvider
    {
        public readonly Channel<AsrEvent> Events = Channel.CreateUnbounded<AsrEvent>();

        public string Id => "draining";

        public AsrCapabilities Caps { get; } = new(
            Streaming: true, Diarization: true, Translation: false,
            AutoLanguageDetect: true, new HashSet<string> { "zh", "de" }, LatencyClass.Realtime);

        public Task<IAsrSession> StartAsync(AsrSessionOptions options, CancellationToken ct) =>
            Task.FromResult<IAsrSession>(new Session(Events));

        private sealed class Session(Channel<AsrEvent> events) : IAsrSession
        {
            public IAsyncEnumerable<AsrEvent> Events => ReadAsync();

            public ValueTask PushAudioAsync(ReadOnlyMemory<byte> pcm16, CancellationToken ct = default) =>
                ValueTask.CompletedTask;

            public ValueTask DisposeAsync() => ValueTask.CompletedTask; // the channel drains on

            private async IAsyncEnumerable<AsrEvent> ReadAsync(
                [EnumeratorCancellation] CancellationToken ct = default)
            {
                await foreach (var e in events.Reader.ReadAllAsync(ct))
                    yield return e;
            }
        }
    }

    /// <summary>
    /// First call: a decode the test holds open and releases, so the grace window is open for
    /// exactly as long as the test needs. Second call: a decode caught by the cancel, which —
    /// like a native one — takes a moment to unwind after the token fires.
    /// </summary>
    private sealed class DrainWindowMt : IMtProvider
    {
        private int _calls;
        private readonly TaskCompletionSource _firstStarted =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _firstRelease =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _secondStarted =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task FirstStarted => _firstStarted.Task;
        public Task SecondStarted => _secondStarted.Task;
        public volatile bool LateUnwound;

        public void ReleaseFirst() => _firstRelease.TrySetResult();

        public string Id => "drain-window";

        public async Task<IReadOnlyDictionary<string, string>> TranslateAsync(
            string text, string from, IReadOnlyList<string> to,
            IReadOnlyList<Utterance> context, CancellationToken ct)
        {
            if (Interlocked.Increment(ref _calls) == 1)
            {
                _firstStarted.TrySetResult();
                await _firstRelease.Task;
                return to.ToDictionary(l => l, l => $"{l}:{text}");
            }

            _secondStarted.TrySetResult();
            try
            {
                await Task.Delay(Timeout.Infinite, ct);
            }
            catch (OperationCanceledException)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(250), CancellationToken.None);
                LateUnwound = true;
                throw;
            }

            return new Dictionary<string, string>();
        }
    }

    private static AsrEvent.Transcript Final(string id, string text) => new(
        id, "S01", text, "zh", 0, 10, IsFinal: true, CodeSwitch: false,
        SpeakerConfidence: 0.9, Translations: null);

    private static readonly FakeAsrProvider.Line[] OneLineScript =
    [
        new("S01", "zh", "料号 KX-4402 确认。"),
    ];

    private static FakeAsrProvider FastFake(bool translation = false) => new(
        OneLineScript,
        partialInterval: TimeSpan.FromMilliseconds(5),
        caps: new AsrCapabilities(
            Streaming: true, Diarization: true, Translation: translation,
            AutoLanguageDetect: true, new HashSet<string> { "zh", "de" }, LatencyClass.Realtime));

    private static async Task RunToEndAsync(MeetingSession session)
    {
        var ended = new TaskCompletionSource();
        session.SessionEnded += _ => ended.TrySetResult();
        await session.StartAsync();
        await ended.Task.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task DecoupledProviderRoutesFinalsThroughMt()
    {
        var relay = new RecordingRelay();
        var mt = new CountingMt();
        await using var session = new MeetingSession(
            FastFake(), mt, relay, new RoomConfig("t", ["zh", "de"]));

        await RunToEndAsync(session);
        await session.DisposeAsync(); // waits for pending translations

        Assert.Equal(1, mt.Calls);
        var translation = Assert.Single(relay.OfType<TranslationUpsert>());
        Assert.Equal("de:料号 KX-4402 确认。", translation.Translations["de"]);
        // partials and the final all went out as upserts for the same id
        var upserts = relay.OfType<UtteranceUpsert>();
        Assert.True(upserts.Count > 1);
        Assert.Single(upserts.Select(u => u.Utterance.Id).Distinct());
    }

    [Fact]
    public async Task EndToEndProviderNeverCallsMt()
    {
        var relay = new RecordingRelay();
        var mt = new CountingMt();
        await using var session = new MeetingSession(
            FastFake(translation: true), mt, relay, new RoomConfig("t", ["zh", "de"]));

        await RunToEndAsync(session);

        Assert.Equal(0, mt.Calls);
    }

    [Fact]
    public void DecoupledProviderWithoutMtIsRejected()
    {
        Assert.Throws<ArgumentException>(() => new MeetingSession(
            FastFake(), mt: null, new NullRelayPublisher(), new RoomConfig("t", ["zh"])));
    }

    [Fact]
    public async Task ConfigIsPublishedOnStart()
    {
        var relay = new RecordingRelay();
        await using var session = new MeetingSession(
            FastFake(translation: true), null, relay, new RoomConfig("room-1", ["zh", "de"]));

        await RunToEndAsync(session);

        var config = Assert.Single(relay.OfType<RoomConfigMessage>());
        Assert.Equal("room-1", config.Config.RoomId);
    }

    /// <summary>
    /// Stop must return promptly whatever the translator is doing. Disposal used to await every
    /// pending translation with no cancellation at all, so Stop was held open for as long as a
    /// decode took — with a local model that is seconds at best and, when the model spends its
    /// whole budget reasoning, the twenty-second freeze this was reported as. The grace is for
    /// a translation that is nearly done; past it the operator's Stop wins.
    /// </summary>
    [Fact]
    public async Task DisposeCancelsATranslationThatOutlastsTheGrace()
    {
        var mt = new BlockingMt();
        var session = new MeetingSession(
            FastFake(), mt, new RecordingRelay(), new RoomConfig("t", ["zh", "de"]),
            translationGrace: TimeSpan.FromMilliseconds(50));

        await session.StartAsync();
        await mt.Started.WaitAsync(TimeSpan.FromSeconds(10));

        var started = Environment.TickCount64;
        await session.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        var elapsed = Environment.TickCount64 - started;

        Assert.True(elapsed < 2000, $"Stop was held open for {elapsed} ms.");
        Assert.True(mt.Cancelled, "the in-flight translation was never cancelled.");
    }

    /// <summary>
    /// The other half of the bargain: a translation that lands inside the grace is still
    /// applied, so the last sentence of a meeting reaches the export rather than being
    /// thrown away for arriving a few hundred milliseconds late.
    /// </summary>
    [Fact]
    public async Task DisposeStillCollectsATranslationThatLandsInsideTheGrace()
    {
        var relay = new RecordingRelay();
        var session = new MeetingSession(
            FastFake(), new SlowMt(), relay, new RoomConfig("t", ["zh", "de"]),
            translationGrace: TimeSpan.FromSeconds(5));

        await RunToEndAsync(session);
        Assert.Empty(relay.OfType<TranslationUpsert>()); // still decoding when Stop arrives

        await session.DisposeAsync();

        Assert.NotEmpty(relay.OfType<TranslationUpsert>());
    }

    /// <summary>
    /// The pending snapshot is taken while the pump may still be draining finals that were
    /// buffered before Stop. A translation tracked in that window is cancelled with the rest
    /// but is absent from the snapshot, so disposal could return — and the caller free the
    /// native weights — while that decode was still unwinding, with the disposed _cts firing
    /// spurious errors behind it. Disposal must not return until every tracked translation,
    /// however late it was tracked, has finished unwinding.
    /// </summary>
    [Fact]
    public async Task DisposeAwaitsATranslationTrackedWhileThePumpWasStillDraining()
    {
        var asr = new DrainingAsr();
        var mt = new DrainWindowMt();
        var session = new MeetingSession(
            asr, mt, new RecordingRelay(), new RoomConfig("t", ["zh", "de"]),
            translationGrace: TimeSpan.FromSeconds(5));

        await session.StartAsync();
        await asr.Events.Writer.WriteAsync(Final("u1", "第一句。"));
        await mt.FirstStarted.WaitAsync(TimeSpan.FromSeconds(10)); // …is pending at the snapshot

        // Disposal runs synchronously up to the grace wait, so once we regain control the
        // pending set has been snapshotted — anything the pump tracks from here on is late.
        var disposing = session.DisposeAsync().AsTask();
        await asr.Events.Writer.WriteAsync(Final("u2", "第二句。"));
        await mt.SecondStarted.WaitAsync(TimeSpan.FromSeconds(10));

        mt.ReleaseFirst(); // first lands inside the grace; the cancel follows and catches u2
        await disposing.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.True(mt.LateUnwound,
            "disposal returned while a late-tracked translation was still unwinding.");
    }
}
