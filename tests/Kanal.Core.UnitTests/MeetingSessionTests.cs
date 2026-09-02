using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Kanal.Core.Models;
using Kanal.Core.Providers;
using Kanal.Core.Providers.Testing;
using Kanal.Core.Relay;
using Kanal.Core.Room;

namespace Kanal.Core.UnitTests;

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

    /// <summary>Records what actually reached the wire, so "nothing left the machine" is testable.</summary>
    private sealed class AudioCountingAsr : IAsrProvider
    {
        public readonly Session Pushes = new();

        public string Id => "audio-counting";

        public AsrCapabilities Caps { get; } = new(
            Streaming: true, Diarization: false, Translation: true,
            AutoLanguageDetect: true, new HashSet<string> { "zh" }, LatencyClass.Realtime);

        public Task<IAsrSession> StartAsync(AsrSessionOptions options, CancellationToken ct) =>
            Task.FromResult<IAsrSession>(Pushes);

        internal sealed class Session : IAsrSession
        {
            private readonly Channel<AsrEvent> _events = Channel.CreateUnbounded<AsrEvent>();

            public int Frames;

            public ValueTask PushAudioAsync(ReadOnlyMemory<byte> pcm16, CancellationToken ct = default)
            {
                Interlocked.Increment(ref Frames);
                return ValueTask.CompletedTask;
            }

            /// <summary>Says nothing until disposed — this fake exists to count audio, not to speak.</summary>
            public IAsyncEnumerable<AsrEvent> Events => _events.Reader.ReadAllAsync();

            public ValueTask DisposeAsync()
            {
                _events.Writer.TryComplete();
                return ValueTask.CompletedTask;
            }
        }
    }

    /// <summary>An ASR the test speaks through directly, so event timing against a pause is exact.</summary>
    private sealed class HandDrivenAsr : IAsrProvider
    {
        public readonly Session Feed = new();

        public string Id => "hand-driven";

        public AsrCapabilities Caps { get; } = new(
            Streaming: true, Diarization: true, Translation: true,
            AutoLanguageDetect: true, new HashSet<string> { "zh", "de" }, LatencyClass.Realtime);

        public Task<IAsrSession> StartAsync(AsrSessionOptions options, CancellationToken ct) =>
            Task.FromResult<IAsrSession>(Feed);

        internal sealed class Session : IAsrSession
        {
            private readonly Channel<AsrEvent> _events = Channel.CreateUnbounded<AsrEvent>();

            public ValueTask SayAsync(AsrEvent e) => _events.Writer.WriteAsync(e);

            public ValueTask PushAudioAsync(ReadOnlyMemory<byte> pcm16, CancellationToken ct = default) =>
                ValueTask.CompletedTask;

            public IAsyncEnumerable<AsrEvent> Events => _events.Reader.ReadAllAsync();

            public ValueTask DisposeAsync()
            {
                _events.Writer.TryComplete();
                return ValueTask.CompletedTask;
            }
        }
    }

    private static AsrEvent.Transcript Spoken(string id, string text, bool isFinal) => new(
        id, "S01", text, "zh", 0, isFinal ? 900 : null, isFinal,
        CodeSwitch: false, SpeakerConfidence: 0.9, Translations: null);

    private static async Task WaitUntilAsync(Func<bool> condition, int timeoutMs = 5_000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (!condition())
        {
            if (Environment.TickCount64 > deadline)
                throw new TimeoutException("Condition not met in time.");
            await Task.Delay(10);
        }
    }

    /// <summary>
    /// The audio gate means everything the ASR sends during a pause derives from audio that was
    /// pushed before it — words spoken on the record. The operator pauses the moment the other
    /// side finishes a sentence; the transcriber flushes that sentence's final a beat later.
    /// Dropping it would leave the last on-record sentence a muted partial on every phone and in
    /// the export forever, and its translation would never be requested: content loss, with no
    /// privacy bought in exchange. A sentence that began on the record may finish on it.
    /// </summary>
    [Fact]
    public async Task ASentenceBegunOnTheRecordStillGetsItsFinalDuringPause()
    {
        var asr = new HandDrivenAsr();
        var relay = new RecordingRelay();
        await using var session = new MeetingSession(
            asr, null, relay, new RoomConfig("t", ["zh"]));
        await session.StartAsync();

        await asr.Feed.SayAsync(Spoken("u1", "料号 KX-", isFinal: false));
        await WaitUntilAsync(() => relay.OfType<UtteranceUpsert>().Count == 1);

        await session.SetPausedAsync(true);
        await asr.Feed.SayAsync(Spoken("u1", "料号 KX-4402 确认。", isFinal: true));

        await WaitUntilAsync(() =>
            relay.OfType<UtteranceUpsert>().Any(u => u.Utterance.State == UtteranceState.Final));
        var recorded = Assert.Single(session.Room.Snapshot().Utterances);
        Assert.Equal("料号 KX-4402 确认。", recorded.SrcText);
    }

    /// <summary>
    /// The other side of the same line: an utterance that *begins* while paused never enters,
    /// partial or final. This is what keeps the scripted provider — which generates its own
    /// audio and talks straight through a pause — off the record.
    /// </summary>
    [Fact]
    public async Task ASentenceBegunWhilePausedNeverEnters()
    {
        var asr = new HandDrivenAsr();
        var relay = new RecordingRelay();
        await using var session = new MeetingSession(
            asr, null, relay, new RoomConfig("t", ["zh"]));
        await session.StartAsync();

        await session.SetPausedAsync(true);
        await asr.Feed.SayAsync(Spoken("u2", "内部", isFinal: false));
        await asr.Feed.SayAsync(Spoken("u2", "内部商量。", isFinal: true));

        // a sentinel error is processed even while paused, so once it surfaces the pump has
        // already read (and dropped) everything written before it — only then is it safe to
        // resume without racing the reader
        var sentinel = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        session.ErrorOccurred += _ => sentinel.TrySetResult();
        await asr.Feed.SayAsync(new AsrEvent.Error("sentinel", Fatal: false));
        await sentinel.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await session.SetPausedAsync(false);
        await asr.Feed.SayAsync(Spoken("u3", "继续。", isFinal: true));

        await WaitUntilAsync(() => relay.OfType<UtteranceUpsert>().Any(u => u.Utterance.Id == "u3"));
        Assert.DoesNotContain(relay.OfType<UtteranceUpsert>(), u => u.Utterance.Id == "u2");
        Assert.DoesNotContain(session.Room.Snapshot().Utterances, u => u.Id == "u2");
    }

    /// <summary>
    /// The half that makes pause a privacy control rather than a display one. Dropping
    /// transcripts while still streaming the room to a cloud transcriber would mean the audio
    /// of the private conversation left the building and only the transcript of it was hidden —
    /// worse than not offering pause at all. A paused session accepts no audio.
    /// </summary>
    [Fact]
    public async Task APausedSessionAcceptsNoAudio()
    {
        var asr = new AudioCountingAsr();
        await using var session = new MeetingSession(
            asr, null, new RecordingRelay(), new RoomConfig("t", ["zh"]));
        await session.StartAsync();

        await session.PushAudioAsync(new byte[320]);
        Assert.Equal(1, asr.Pushes.Frames);

        await session.SetPausedAsync(true);
        await session.PushAudioAsync(new byte[320]);
        await session.PushAudioAsync(new byte[320]);
        Assert.Equal(1, asr.Pushes.Frames);

        await session.SetPausedAsync(false);
        await session.PushAudioAsync(new byte[320]);
        Assert.Equal(2, asr.Pushes.Frames);
    }

    /// <summary>
    /// The recorder hangs off this tap rather than off the capture loop, so "paused means it is
    /// not being recorded" is structurally true instead of remembered in a second place.
    /// </summary>
    [Fact]
    public async Task AudioAcceptedFiresForWhatWasTakenAndNothingElse()
    {
        var asr = new AudioCountingAsr();
        var taken = 0;
        await using var session = new MeetingSession(
            asr, null, new RecordingRelay(), new RoomConfig("t", ["zh"]));
        session.AudioAccepted += _ => Interlocked.Increment(ref taken);
        await session.StartAsync();

        await session.PushAudioAsync(new byte[320]);
        Assert.Equal(1, taken);

        await session.SetPausedAsync(true);
        await session.PushAudioAsync(new byte[320]);
        await session.PushAudioAsync(new byte[320]);
        Assert.Equal(1, taken);

        await session.SetPausedAsync(false);
        await session.PushAudioAsync(new byte[320]);
        Assert.Equal(2, taken);
        Assert.Equal(taken, asr.Pushes.Frames); // the tap and the wire see exactly the same audio
    }

    /// <summary>
    /// Pause is a privacy control before it is a convenience one: in a supplier negotiation the
    /// operator steps out of the meeting to talk to their own side, and nothing said in that
    /// minute may be transcribed, translated, published to the phones in the room, or — in a
    /// cloud mode — sent off this machine at all.
    /// </summary>
    [Fact]
    public async Task NothingSpokenWhilePausedIsRecordedOrPublished()
    {
        var relay = new RecordingRelay();
        await using var session = new MeetingSession(
            FastFake(translation: true), null, relay, new RoomConfig("t", ["zh", "de"]));

        await session.SetPausedAsync(true);
        await RunToEndAsync(session);

        Assert.Empty(relay.OfType<UtteranceUpsert>());
        Assert.Empty(session.Room.Snapshot().Utterances);
    }

    [Fact]
    public async Task ResumingRecordsAgain()
    {
        var relay = new RecordingRelay();
        await using var session = new MeetingSession(
            FastFake(translation: true), null, relay, new RoomConfig("t", ["zh", "de"]));

        await session.SetPausedAsync(true);
        await session.SetPausedAsync(false);
        await RunToEndAsync(session);

        Assert.NotEmpty(relay.OfType<UtteranceUpsert>());
    }

    /// <summary>
    /// A phone whose column simply stops is indistinguishable from a phone whose connection
    /// broke. The room is told, so the page can say "paused" rather than leaving the
    /// participant to guess — the same reasoning as <c>room.closed</c>.
    /// </summary>
    [Fact]
    public async Task PauseAndResumeAreAnnouncedToTheRoom()
    {
        var relay = new RecordingRelay();
        await using var session = new MeetingSession(
            FastFake(translation: true), null, relay, new RoomConfig("t", ["zh", "de"]));

        await session.SetPausedAsync(true);
        await session.SetPausedAsync(false);

        var announced = relay.OfType<RoomPausedMessage>();
        Assert.Equal([true, false], announced.Select(m => m.Paused));
    }

    /// <summary>Setting pause to what it already is says nothing — a repeated press must not
    /// fill the channel with messages the clients would only re-apply.</summary>
    [Fact]
    public async Task SettingTheSamePauseStateTwiceIsNotAnnouncedTwice()
    {
        var relay = new RecordingRelay();
        await using var session = new MeetingSession(
            FastFake(translation: true), null, relay, new RoomConfig("t", ["zh", "de"]));

        await session.SetPausedAsync(true);
        await session.SetPausedAsync(true);

        Assert.Single(relay.OfType<RoomPausedMessage>());
    }

    /// <summary>A phone joining mid-pause has missed the announcement; the snapshot carries it,
    /// so late join lands in the same state as everyone else — the room.snapshot invariant.</summary>
    [Fact]
    public async Task SnapshotCarriesThePausedState()
    {
        var relay = new RecordingRelay();
        await using var session = new MeetingSession(
            FastFake(translation: true), null, relay, new RoomConfig("t", ["zh", "de"]));

        await session.SetPausedAsync(true);
        await session.PublishSnapshotAsync();

        Assert.True(relay.OfType<RoomSnapshotMessage>().Single().Snapshot.Paused);
    }

    /// <summary>
    /// Whether the room is being recorded to audio is the participants' business, not only the
    /// operator's: two of the three languages in the room are spoken in jurisdictions where
    /// recording a private conversation without the other side knowing is a criminal matter,
    /// and the phone in their hand is the only surface they read.
    /// </summary>
    [Fact]
    public async Task RecordingIsAnnouncedToTheRoom()
    {
        var relay = new RecordingRelay();
        await using var session = new MeetingSession(
            FastFake(translation: true), null, relay, new RoomConfig("t", ["zh", "de"]));

        await session.SetRecordingAsync(true);
        await session.SetRecordingAsync(false);

        Assert.Equal([true, false], relay.OfType<RoomRecordingMessage>().Select(m => m.Recording));
    }

    /// <summary>Same reasoning as pause: a repeated setting must not fill the channel.</summary>
    [Fact]
    public async Task SettingTheSameRecordingStateTwiceIsNotAnnouncedTwice()
    {
        var relay = new RecordingRelay();
        await using var session = new MeetingSession(
            FastFake(translation: true), null, relay, new RoomConfig("t", ["zh", "de"]));

        await session.SetRecordingAsync(true);
        await session.SetRecordingAsync(true);

        Assert.Single(relay.OfType<RoomRecordingMessage>());
    }

    /// <summary>
    /// A phone that scans the QR ten minutes in never saw the announcement. Late join is served
    /// entirely from the snapshot, so a notice that only existed as an event would be a notice
    /// most participants never get.
    /// </summary>
    [Fact]
    public async Task SnapshotCarriesTheRecordingState()
    {
        var relay = new RecordingRelay();
        await using var session = new MeetingSession(
            FastFake(translation: true), null, relay, new RoomConfig("t", ["zh", "de"]));

        await session.SetRecordingAsync(true);
        await session.PublishSnapshotAsync();

        Assert.True(relay.OfType<RoomSnapshotMessage>().Single().Snapshot.Recording);
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
