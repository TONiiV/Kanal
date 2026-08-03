using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Kanal.Core.Models;
using Kanal.Core.Providers;
using Kanal.Core.Relay;
using Kanal.Core.Room;

namespace Kanal.Tests;

/// <summary>
/// The host is the single authority: whatever script the ASR or MT hands back,
/// Chinese text is Simplified by the time it reaches <see cref="RoomState"/> or
/// the relay. Clients are projections and must never need to convert.
/// </summary>
public class ChineseOutputNormalizationTests
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

    /// <summary>An ASR the test speaks through directly — the Gladia stand-in.</summary>
    private sealed class HandDrivenAsr(bool translation) : IAsrProvider
    {
        public readonly Session Feed = new();

        public string Id => "hand-driven";

        public AsrCapabilities Caps { get; } = new(
            Streaming: true, Diarization: true, Translation: translation,
            AutoLanguageDetect: true, new HashSet<string> { "zh", "de", "pl" }, LatencyClass.Realtime);

        public Task<IAsrSession> StartAsync(AsrSessionOptions options, CancellationToken ct) =>
            Task.FromResult<IAsrSession>(Feed);

        internal sealed class Session : IAsrSession
        {
            private readonly Channel<AsrEvent> _events = Channel.CreateUnbounded<AsrEvent>();

            public ValueTask SayAsync(AsrEvent e) => _events.Writer.WriteAsync(e);

            public ValueTask PushAudioAsync(ReadOnlyMemory<byte> pcm16, CancellationToken ct = default) =>
                ValueTask.CompletedTask;

            public IAsyncEnumerable<AsrEvent> Events => ReadAsync();

            public ValueTask DisposeAsync()
            {
                _events.Writer.TryComplete();
                return ValueTask.CompletedTask;
            }

            private async IAsyncEnumerable<AsrEvent> ReadAsync(
                [EnumeratorCancellation] CancellationToken ct = default)
            {
                await foreach (var e in _events.Reader.ReadAllAsync(ct))
                    yield return e;
            }
        }
    }

    /// <summary>Fake MT that answers with a fixed dictionary — the Traditional-emitting model.</summary>
    private sealed class ScriptedMt(IReadOnlyDictionary<string, string> answers) : IMtProvider
    {
        public string Id => "scripted";

        public Task<IReadOnlyDictionary<string, string>> TranslateAsync(
            string text, string from, IReadOnlyList<string> to,
            IReadOnlyList<Utterance> context, CancellationToken ct) =>
            Task.FromResult(answers);
    }

    private static AsrEvent.Transcript Spoken(
        string id, string lang, string text, bool isFinal,
        IReadOnlyDictionary<string, string>? translations = null) => new(
        id, "S01", text, lang, 0, isFinal ? 900 : null, isFinal,
        CodeSwitch: false, SpeakerConfidence: 0.9, Translations: translations);

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

    [Fact]
    public async Task ChineseTranscriptsArePublishedSimplified_PartialsAndFinals()
    {
        var asr = new HandDrivenAsr(translation: true);
        var relay = new RecordingRelay();
        await using var session = new MeetingSession(
            asr, null, relay, new RoomConfig("t", ["zh", "de"]));
        await session.StartAsync();

        await asr.Feed.SayAsync(Spoken("u1", "zh", "這是一個", isFinal: false));
        await asr.Feed.SayAsync(Spoken("u1", "zh", "這是一個測試,料號 KX-4402。", isFinal: true));
        await WaitUntilAsync(() =>
            relay.OfType<UtteranceUpsert>().Any(u => u.Utterance.State == UtteranceState.Final));

        var upserts = relay.OfType<UtteranceUpsert>();
        Assert.Equal("这是一个", upserts[0].Utterance.SrcText);
        Assert.Equal("这是一个测试,料号 KX-4402。", upserts[^1].Utterance.SrcText);
        Assert.Equal(
            "这是一个测试,料号 KX-4402。",
            Assert.Single(session.Room.Snapshot().Utterances).SrcText);
    }

    [Fact]
    public async Task ChineseTranslationsFromTheAsrArePublishedSimplified()
    {
        var asr = new HandDrivenAsr(translation: true);
        var relay = new RecordingRelay();
        await using var session = new MeetingSession(
            asr, null, relay, new RoomConfig("t", ["zh", "de"]));
        await session.StartAsync();

        await asr.Feed.SayAsync(Spoken(
            "u1", "de", "Die Übertragungssteuerung ist bestätigt.", isFinal: true,
            translations: new Dictionary<string, string>
            {
                ["zh"] = "傳輸控制已確認。",
            }));
        await WaitUntilAsync(() => relay.OfType<UtteranceUpsert>().Count == 1);

        var published = relay.OfType<UtteranceUpsert>()[0].Utterance;
        Assert.Equal("传输控制已确认。", published.Translations["zh"]);
        Assert.Equal("Die Übertragungssteuerung ist bestätigt.", published.SrcText);
        Assert.Equal(
            "传输控制已确认。",
            Assert.Single(session.Room.Snapshot().Utterances).Translations["zh"]);
    }

    [Fact]
    public async Task ChineseFromTheMtProviderIsPublishedSimplified()
    {
        var asr = new HandDrivenAsr(translation: false);
        var relay = new RecordingRelay();
        var mt = new ScriptedMt(new Dictionary<string, string>
        {
            ["zh"] = "傳輸控制已確認。",
            ["pl"] = "Sterowanie transmisją potwierdzone.",
        });
        await using var session = new MeetingSession(
            asr, mt, relay, new RoomConfig("t", ["zh", "de", "pl"]));
        await session.StartAsync();

        await asr.Feed.SayAsync(Spoken(
            "u1", "de", "Die Übertragungssteuerung ist bestätigt.", isFinal: true));
        await WaitUntilAsync(() => relay.OfType<TranslationUpsert>().Count == 1);

        var published = relay.OfType<TranslationUpsert>()[0];
        Assert.Equal("传输控制已确认。", published.Translations["zh"]);
        Assert.Equal("Sterowanie transmisją potwierdzone.", published.Translations["pl"]);
        Assert.Equal(
            "传输控制已确认。",
            Assert.Single(session.Room.Snapshot().Utterances).Translations["zh"]);
    }

    [Fact]
    public async Task NonChineseTextIsUntouched()
    {
        var asr = new HandDrivenAsr(translation: true);
        var relay = new RecordingRelay();
        await using var session = new MeetingSession(
            asr, null, relay, new RoomConfig("t", ["zh", "pl"]));
        await session.StartAsync();

        const string polish = "Czy próbki wsporników będą zgodne z normą ISO 7599?";
        await asr.Feed.SayAsync(Spoken("u1", "pl", polish, isFinal: true));
        await WaitUntilAsync(() => relay.OfType<UtteranceUpsert>().Count == 1);

        Assert.Equal(polish, relay.OfType<UtteranceUpsert>()[0].Utterance.SrcText);
    }
}
