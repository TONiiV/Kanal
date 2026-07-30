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
}
