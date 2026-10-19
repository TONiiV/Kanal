using System.Threading.Channels;
using Kanal.Core.Models;
using Kanal.Core.Providers;
using Kanal.Core.Relay;
using Kanal.Core.Room;

namespace Kanal.Core.UnitTests;

public class MtErrorCodeTests
{
    private sealed class NullRelay : IRelayPublisher
    {
        public Task PublishAsync(RelayMessage message, CancellationToken ct = default) => Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class TextOnlyAsr : IAsrProvider
    {
        public readonly Channel<AsrEvent> Events = Channel.CreateUnbounded<AsrEvent>();

        public string Id => "text-only";

        public AsrCapabilities Caps { get; } = new(
            Streaming: true, Diarization: true, Translation: false,
            AutoLanguageDetect: true, new HashSet<string> { "zh", "de" }, LatencyClass.Realtime);

        public Task<IAsrSession> StartAsync(AsrSessionOptions options, CancellationToken ct) =>
            Task.FromResult<IAsrSession>(new Session(Events));

        private sealed class Session(Channel<AsrEvent> events) : IAsrSession
        {
            public ValueTask PushAudioAsync(ReadOnlyMemory<byte> pcm16, CancellationToken ct = default) =>
                ValueTask.CompletedTask;

            public IAsyncEnumerable<AsrEvent> Events => events.Reader.ReadAllAsync();

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    private sealed class FailingMt(Exception failure) : IMtProvider
    {
        public string Id => "failing";

        public Task<IReadOnlyDictionary<string, string>> TranslateAsync(
            string text, string from, IReadOnlyList<string> to,
            IReadOnlyList<Utterance> context, CancellationToken ct) =>
            throw failure;
    }

    private static async Task<AsrEvent.Error> FirstErrorAsync(Exception failure)
    {
        var asr = new TextOnlyAsr();
        await using var session = new MeetingSession(
            asr, new FailingMt(failure), new NullRelay(), new RoomConfig("t", ["zh", "de"]));
        var error = new TaskCompletionSource<AsrEvent.Error>(TaskCreationOptions.RunContinuationsAsynchronously);
        session.ErrorOccurred += e => error.TrySetResult(e);
        await session.StartAsync(TestContext.Current.CancellationToken);

        await asr.Events.Writer.WriteAsync(new AsrEvent.Transcript(
            "u1", "S01", "你好。", "zh", 0, 900, true,
            CodeSwitch: false, SpeakerConfidence: 0.9, Translations: null));

        return await error.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task AProviderFailureWithACodeReachesTheHostWithThatCode()
    {
        var error = await FirstErrorAsync(new MtProviderException("quota", MtProviderException.QuotaExceeded));

        Assert.Equal(MtProviderException.QuotaExceeded, error.Code);
        Assert.False(error.Fatal);
    }

    [Fact]
    public async Task AnyOtherFailureHasNoCode()
    {
        var error = await FirstErrorAsync(new InvalidOperationException("boom"));

        Assert.Null(error.Code);
        Assert.Contains("boom", error.Message);
    }
}
