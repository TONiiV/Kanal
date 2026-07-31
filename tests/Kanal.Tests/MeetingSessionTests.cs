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
}
