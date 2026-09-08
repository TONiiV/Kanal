using System.Threading.Channels;
using Kanal.Core.Meetings;
using Kanal.Core.Models;
using Kanal.Core.Providers;
using Kanal.Core.Relay;
using Kanal.Core.Room;

namespace Kanal.Core.UnitTests;

public class MeetingTimelineTests
{
    private const int BytesPerSecond = 32_000;

    private static byte[] Audio(double seconds, long fromByte = 0)
    {
        var bytes = new byte[(int)(seconds * BytesPerSecond)];
        for (var i = 0; i < bytes.Length; i++)
            bytes[i] = (byte)((fromByte + i) % 251);
        return bytes;
    }

    [Fact]
    public void AFinalisedUtteranceMapsToAByteRangeOfTheAudioAccepted()
    {
        var timeline = new MeetingTimeline();
        timeline.Append(Audio(2));

        timeline.Observe("u1", 500, 1_500);

        var span = timeline.Locate("u1");
        Assert.NotNull(span);
        Assert.Equal(16_000, span.StartByte);
        Assert.Equal(48_000, span.EndByte);
        Assert.False(span.EndIsOpen);
    }

    [Fact]
    public void AnUnknownUtteranceIsNotGuessedAt()
    {
        var timeline = new MeetingTimeline();
        timeline.Append(Audio(2));

        Assert.Null(timeline.Locate("never-seen"));
        Assert.Equal(AudioAvailability.UnknownUtterance, timeline.LocateInRecording("never-seen").Availability);
        Assert.Equal(AudioAvailability.UnknownUtterance, timeline.TakeRecentAudio("never-seen").Availability);
    }

    /// <summary>
    /// The whole point of hanging off <see cref="MeetingSession.AudioAccepted"/>: wall-clock time
    /// is not the timeline. A minute off the record leaves no trace on it, so the sentence after
    /// the pause lands where its audio actually is rather than a minute further on.
    /// </summary>
    [Fact]
    public async Task APauseInTheMiddleDoesNotShiftTheUtterancesAfterIt()
    {
        var asr = new HandDrivenAsr();
        await using var session = new MeetingSession(
            asr, null, new NullRelay(), new RoomConfig("t", ["zh"]));
        await session.StartAsync(TestContext.Current.CancellationToken);

        await session.PushAudioAsync(Audio(1));
        await session.SetPausedAsync(true);
        await session.PushAudioAsync(Audio(1));
        await session.SetPausedAsync(false);
        await session.PushAudioAsync(Audio(1));

        // the transcriber counted two seconds of audio, because two is all it was given
        await asr.Feed.SayAsync(Final("u1", tStartMs: 1_000, tEndMs: 2_000));
        await WaitUntilAsync(() => session.Timeline.Locate("u1") is not null);

        Assert.Equal(TimeSpan.FromSeconds(2), session.Timeline.Duration);
        var span = session.Timeline.Locate("u1");
        Assert.NotNull(span);
        Assert.Equal(32_000, span.StartByte);
        Assert.Equal(64_000, span.EndByte);
    }

    [Fact]
    public void AnAsrClockThatRestartsDoesNotDragLaterUtterancesBackToTheStart()
    {
        var timeline = new MeetingTimeline();
        timeline.Append(Audio(10));

        timeline.AsrClockRestarted();
        timeline.Append(Audio(2));
        timeline.Observe("after", 0, 1_000);

        var span = timeline.Locate("after");
        Assert.NotNull(span);
        Assert.Equal(320_000, span.StartByte);
        Assert.Equal(352_000, span.EndByte);
    }

    /// <summary>
    /// A restart between a sentence's partial and its final would otherwise re-place the sentence
    /// on the new clock and move it forwards by the whole meeting so far.
    /// </summary>
    [Fact]
    public void AnUtteranceStaysOnTheClockItWasFirstSeenOn()
    {
        var timeline = new MeetingTimeline();
        timeline.Append(Audio(4));
        timeline.Observe("u1", 3_000, null);

        timeline.AsrClockRestarted();
        timeline.Append(Audio(2));
        timeline.Observe("u1", 3_000, 3_800);

        var span = timeline.Locate("u1");
        Assert.NotNull(span);
        Assert.Equal(96_000, span.StartByte);
        Assert.Equal(121_600, span.EndByte);
    }

    [Fact]
    public void AnOpenEndedUtteranceRunsToTheAudioAcceptedSoFar()
    {
        var timeline = new MeetingTimeline();
        timeline.Append(Audio(2));

        timeline.Observe("u1", 500, null);

        var span = timeline.Locate("u1");
        Assert.NotNull(span);
        Assert.Equal(16_000, span.StartByte);
        Assert.Equal(64_000, span.EndByte);
        Assert.True(span.EndIsOpen);
    }

    [Fact]
    public void AnOpenEndedUtteranceDoesNotGrowUntilTheTranscriberSaysSoAgain()
    {
        var timeline = new MeetingTimeline();
        timeline.Append(Audio(2));
        timeline.Observe("u1", 500, null);

        timeline.Append(Audio(1));

        Assert.Equal(64_000, timeline.Locate("u1")!.EndByte);
        timeline.Observe("u1", 500, null);
        Assert.Equal(96_000, timeline.Locate("u1")!.EndByte);
    }

    [Fact]
    public void RecentAudioComesBackAsTheSamplesThatWereAccepted()
    {
        var timeline = new MeetingTimeline(TimeSpan.FromSeconds(2));
        timeline.Append(Audio(3));
        timeline.Observe("u1", 2_500, 3_000);

        var audio = timeline.TakeRecentAudio("u1");

        Assert.Equal(AudioAvailability.Available, audio.Availability);
        Assert.Equal(Audio(0.5, fromByte: 80_000), audio.Pcm16.ToArray());
    }

    [Fact]
    public void AFinalWhoseAudioHasAgedOutOfTheBufferSaysSo()
    {
        var timeline = new MeetingTimeline(TimeSpan.FromSeconds(2));
        timeline.Append(Audio(10));

        timeline.Observe("u1", 0, 500);

        Assert.NotNull(timeline.Locate("u1")); // the mapping survives; only the samples are gone
        Assert.Equal(AudioAvailability.AgedOut, timeline.TakeRecentAudio("u1").Availability);
        Assert.True(timeline.TakeRecentAudio("u1").Pcm16.IsEmpty);
    }

    [Fact]
    public void OffsetsAreRelativeToTheRecordingThatWasRunningAtTheTime()
    {
        var timeline = new MeetingTimeline();
        timeline.Append(Audio(1));
        timeline.RecordingStarted("first.wav");
        timeline.Append(Audio(2));
        timeline.Observe("u1", 1_000, 2_000);
        timeline.RecordingStopped();

        timeline.Append(Audio(1));
        timeline.RecordingStarted("second.wav");
        timeline.Append(Audio(2));
        timeline.Observe("u2", 4_000, 5_000);

        var first = timeline.LocateInRecording("u1");
        Assert.Equal(AudioAvailability.Available, first.Availability);
        Assert.Equal("first.wav", first.Path);
        Assert.Equal(0, first.StartDataByte);
        Assert.Equal(32_000, first.EndDataByte);

        var second = timeline.LocateInRecording("u2");
        Assert.Equal(AudioAvailability.Available, second.Availability);
        Assert.Equal("second.wav", second.Path);
        Assert.Equal(0, second.StartDataByte);
        Assert.Equal(32_000, second.EndDataByte);
    }

    [Fact]
    public void AnUtteranceSpokenWhileNothingWasBeingRecordedIsNotRecorded()
    {
        var timeline = new MeetingTimeline();
        timeline.Append(Audio(2));
        timeline.Observe("before", 0, 1_000);
        timeline.RecordingStarted("first.wav");
        timeline.Append(Audio(1));

        Assert.Equal(AudioAvailability.NotRecorded, timeline.LocateInRecording("before").Availability);
        Assert.Equal("", timeline.LocateInRecording("before").Path);
    }

    [Fact]
    public void AnUtteranceThatOutlivedTheRecordingIsClippedToWhatWasWritten()
    {
        var timeline = new MeetingTimeline();
        timeline.RecordingStarted("first.wav");
        timeline.Append(Audio(1));
        timeline.RecordingStopped();
        timeline.Append(Audio(2));
        timeline.Observe("u1", 500, 2_000);

        var located = timeline.LocateInRecording("u1");

        Assert.Equal(AudioAvailability.Available, located.Availability);
        Assert.Equal(16_000, located.StartDataByte);
        Assert.Equal(32_000, located.EndDataByte);
    }

    /// <summary>
    /// A transcriber cannot report audio it was never given, but a rounded-up end timestamp on the
    /// last sentence of a meeting would otherwise hand a replay a range that runs past the file.
    /// </summary>
    [Fact]
    public void ASpanNeverRunsPastTheAudioAccepted()
    {
        var timeline = new MeetingTimeline();
        timeline.Append(Audio(1));

        timeline.Observe("u1", 900, 5_000);

        var span = timeline.Locate("u1");
        Assert.NotNull(span);
        Assert.Equal(28_800, span.StartByte);
        Assert.Equal(32_000, span.EndByte);
    }

    private static AsrEvent.Transcript Final(string id, long tStartMs, long tEndMs) => new(
        id, "S01", "料号 KX-4402 确认。", "zh", tStartMs, tEndMs, IsFinal: true,
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

    private sealed class NullRelay : IRelayPublisher
    {
        public Task PublishAsync(RelayMessage message, CancellationToken ct = default) => Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class HandDrivenAsr : IAsrProvider
    {
        public readonly Session Feed = new();

        public string Id => "hand-driven";

        public AsrCapabilities Caps { get; } = new(
            Streaming: true, Diarization: false, Translation: true,
            AutoLanguageDetect: true, new HashSet<string> { "zh" }, LatencyClass.Realtime);

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
}
