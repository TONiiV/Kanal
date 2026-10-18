using System.Threading.Channels;
using Kanal.Core.Meetings;
using Kanal.Core.Models;
using Kanal.Core.Providers;
using Kanal.Core.Relay;
using Kanal.Core.Room;
using Kanal.Core.Workspaces;

namespace Kanal.Core.UnitTests;

public class LiveSpeakerLabelsTests
{
    private const int BytesPerSecond = 32_000;

    private static byte[] Voice(short voice, double seconds)
    {
        var bytes = new byte[(int)(seconds * BytesPerSecond)];
        for (var i = 0; i < bytes.Length; i += 2)
            BitConverter.TryWriteBytes(bytes.AsSpan(i), voice);
        return bytes;
    }

    private sealed class VoiceEmbedder(Dictionary<short, float[]> voices) : ISpeakerEmbedder
    {
        public int Calls;
        public bool Disposed;
        public Exception? Throws;

        public float[] Embed(float[] samples)
        {
            Interlocked.Increment(ref Calls);
            if (Throws is not null)
                throw Throws;
            return voices[(short)Math.Round(samples[0] * 32768)];
        }

        public void Dispose() => Disposed = true;
    }

    private static VoiceEmbedder ThreeVoices() => new(new()
    {
        [1] = [1, 0, 0],
        [2] = [0, 1, 0],
        [3] = [0, 0, 1],
        // cosine 0.6 to voice 1
        [11] = [0.6f, 0.8f, 0],
        // cosine 0.55 to voice 1
        [12] = [0.55f, 0.8352f, 0],
    });

    [Fact]
    public void EachNewVoiceGetsTheNextTentativeTagAndAReturningVoiceKeepsItsTag()
    {
        var labels = new LiveSpeakerLabels(ThreeVoices());

        Assert.Equal("V01?", labels.Assign(Voice(1, 2)));
        Assert.Equal("V02?", labels.Assign(Voice(2, 2)));
        Assert.Equal("V01?", labels.Assign(Voice(1, 2)));
        Assert.Equal("V03?", labels.Assign(Voice(3, 2)));
        Assert.Equal("V02?", labels.Assign(Voice(2, 2)));
    }

    [Fact]
    public void AShortSentenceGetsNoTagBecauseABackchannelIsUsuallyAnotherSpeaker()
    {
        var embedder = ThreeVoices();
        var labels = new LiveSpeakerLabels(embedder);
        labels.Assign(Voice(1, 2));
        labels.Assign(Voice(2, 2));

        Assert.Null(labels.Assign(Voice(1, 0.9)));
        Assert.Equal(2, embedder.Calls);
    }

    [Fact]
    public void AVoiceBelowTheMeasuredDefaultThresholdIsANewSpeaker()
    {
        var labels = new LiveSpeakerLabels(ThreeVoices());
        labels.Assign(Voice(1, 2));

        Assert.Equal("V02?", labels.Assign(Voice(12, 2)));
    }

    [Fact]
    public void AShortFirstSentenceGetsNoTagRatherThanAGuess()
    {
        var embedder = ThreeVoices();

        Assert.Null(new LiveSpeakerLabels(embedder).Assign(Voice(1, 0.5)));
        Assert.Equal(0, embedder.Calls);
    }

    [Fact]
    public void TheThresholdDecidesBetweenAKnownAndANewSpeaker()
    {
        var loose = new LiveSpeakerLabels(ThreeVoices(), threshold: 0.5);
        loose.Assign(Voice(1, 2));
        Assert.Equal("V01?", loose.Assign(Voice(11, 2)));

        var strict = new LiveSpeakerLabels(ThreeVoices(), threshold: 0.7);
        strict.Assign(Voice(1, 2));
        Assert.Equal("V02?", strict.Assign(Voice(11, 2)));
    }

    [Fact]
    public void ACentroidFollowsADriftingVoice()
    {
        var labels = new LiveSpeakerLabels(new VoiceEmbedder(new()
        {
            [1] = [1, 0, 0],
            [2] = [0.7f, 0.7141f, 0],
            // cosine 0.5 to [1,0,0] alone, about 0.8 to the sum [1.7,0.71,0]
            [3] = [0.5f, 0.866f, 0],
        }));

        Assert.Equal("V01?", labels.Assign(Voice(1, 2)));
        Assert.Equal("V01?", labels.Assign(Voice(2, 2)));
        Assert.Equal("V01?", labels.Assign(Voice(3, 2)));
    }

    [Fact]
    public void ALabelAfterATranslationKeepsTheTranslation()
    {
        var room = new RoomState(new RoomConfig("t", ["zh", "de"]));
        var final = room.ApplyTranscript(Said("u1", isFinal: true));
        room.ApplyTranslations("u1", final.Revision, new Dictionary<string, string> { ["de"] = "Hallo" });

        var labelled = room.AssignSpeaker("u1", "V01?").Utterance!;

        Assert.Equal("Hallo", labelled.Translations["de"]);
        Assert.Equal(final.Revision, labelled.Revision);
    }

    [Fact]
    public void ALabelKeepsTheAsrTagBesideItAndSurvivesARevision()
    {
        var room = new RoomState(new RoomConfig("t", ["zh"]));
        room.ApplyTranscript(Said("u1", isFinal: true));

        var (utterance, speaker) = room.AssignSpeaker("u1", "V01?");
        Assert.Equal("V01?", utterance!.SpeakerTag);
        Assert.Equal("S01", utterance.AsrSpeakerTag);
        Assert.Equal("V01?", speaker!.Tag);

        var revised = room.ApplyTranscript(Said("u1", isFinal: true));
        Assert.Equal("V01?", revised.SpeakerTag);
        Assert.Equal("S01", revised.AsrSpeakerTag);
    }

    [Fact]
    public void TheAsrTagIsKeptInTheTranscriptFile()
    {
        var path = Path.Combine(Path.GetTempPath(), $"kanal-{Guid.NewGuid():N}.jsonl");
        try
        {
            var room = new RoomState(new RoomConfig("t", ["zh"]));
            room.ApplyTranscript(Said("u1", isFinal: true));
            using (var log = new TranscriptLogWriter(path, _ => { }))
                log.Append(room.AssignSpeaker("u1", "V01?").Utterance!);

            var read = Assert.Single(TranscriptLog.Read(path));
            Assert.Equal("V01?", read.SpeakerTag);
            Assert.Equal("S01", read.AsrSpeakerTag);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task AFinalFromAnAsrWithoutDiarizationIsLabelledAndPublished()
    {
        var relay = new RecordingRelay();
        var asr = new HandDrivenAsr(diarization: false);
        var embedder = ThreeVoices();
        await using var session = new MeetingSession(
            asr, null, relay, new RoomConfig("t", ["zh"]), speakerLabels: new LiveSpeakerLabels(embedder));
        await session.StartAsync(TestContext.Current.CancellationToken);

        await session.PushAudioAsync(Voice(1, 2), TestContext.Current.CancellationToken);
        await asr.Feed.SayAsync(Said("u1", isFinal: true, endMs: 2_000));
        await session.PushAudioAsync(Voice(2, 2), TestContext.Current.CancellationToken);
        await asr.Feed.SayAsync(Said("u2", isFinal: true, startMs: 2_000, endMs: 4_000));

        await WaitUntilAsync(() => session.Room.Find("u2")?.SpeakerTag == "V02?");
        Assert.Equal("V01?", session.Room.Find("u1")!.SpeakerTag);
        await WaitUntilAsync(() => relay.OfType<UtteranceUpsert>().Any(m => m.Utterance.SpeakerTag == "V02?"));
        Assert.Contains(relay.OfType<SpeakerUpsert>(), m => m.Speaker.Tag == "V02?");
    }

    [Fact]
    public async Task AnAsrThatDiarizesItselfKeepsItsOwnTags()
    {
        var asr = new HandDrivenAsr(diarization: true);
        var embedder = ThreeVoices();
        var session = new MeetingSession(
            asr, null, new RecordingRelay(), new RoomConfig("t", ["zh"]), speakerLabels: new LiveSpeakerLabels(embedder));
        await session.StartAsync(TestContext.Current.CancellationToken);

        await session.PushAudioAsync(Voice(1, 2), TestContext.Current.CancellationToken);
        await asr.Feed.SayAsync(Said("u1", isFinal: true, endMs: 2_000));
        await WaitUntilAsync(() => session.Room.Contains("u1"));
        await session.DisposeAsync();

        Assert.Equal("S01", session.Room.Find("u1")!.SpeakerTag);
        Assert.Equal(0, embedder.Calls);
        Assert.True(embedder.Disposed);
    }

    [Fact]
    public async Task AFailingEmbedderStopsTheLabelsButNotTheMeeting()
    {
        var asr = new HandDrivenAsr(diarization: false);
        var embedder = ThreeVoices();
        embedder.Throws = new InvalidOperationException("model gone");
        await using var session = new MeetingSession(
            asr, null, new RecordingRelay(), new RoomConfig("t", ["zh"]), speakerLabels: new LiveSpeakerLabels(embedder));
        await session.StartAsync(TestContext.Current.CancellationToken);

        await session.PushAudioAsync(Voice(1, 4), TestContext.Current.CancellationToken);
        await asr.Feed.SayAsync(Said("u1", isFinal: true, endMs: 2_000));
        await asr.Feed.SayAsync(Said("u2", isFinal: true, startMs: 2_000, endMs: 4_000));
        await asr.Feed.SayAsync(Said("u3", isFinal: false, startMs: 4_000));

        await WaitUntilAsync(() => session.Room.Contains("u3"));
        await WaitUntilAsync(() => embedder.Calls == 1);
        await Task.Delay(100, TestContext.Current.CancellationToken);
        Assert.Equal(1, embedder.Calls);
        Assert.Equal("S01", session.Room.Find("u2")!.SpeakerTag);
    }

    private static AsrEvent.Transcript Said(string id, bool isFinal, long startMs = 0, long? endMs = 900) => new(
        id, "S01", "你好", "zh", startMs, isFinal ? endMs : null, isFinal,
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

    private sealed class RecordingRelay : IRelayPublisher
    {
        private readonly List<RelayMessage> _messages = new();

        public Task PublishAsync(RelayMessage message, CancellationToken ct = default)
        {
            lock (_messages)
                _messages.Add(message);
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        public List<T> OfType<T>()
        {
            lock (_messages)
                return _messages.OfType<T>().ToList();
        }
    }

    private sealed class HandDrivenAsr(bool diarization) : IAsrProvider
    {
        public readonly Session Feed = new();

        public string Id => "hand-driven";

        public AsrCapabilities Caps { get; } = new(
            Streaming: true, Diarization: diarization, Translation: true,
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
