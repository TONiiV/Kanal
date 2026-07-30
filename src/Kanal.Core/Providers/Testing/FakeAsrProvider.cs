using System.Runtime.CompilerServices;
using System.Threading.Channels;

namespace Kanal.Core.Providers.Testing;

/// <summary>
/// Scripted ASR provider for UI development and rehearsal without an API key.
/// Emits growing partials followed by a final for each scripted line.
/// Declares Translation = false so the IMtProvider path is exercised.
/// </summary>
public sealed class FakeAsrProvider : IAsrProvider
{
    public sealed record Line(string SpeakerTag, string Lang, string Text, bool CodeSwitch = false);

    public static readonly IReadOnlyList<Line> DefaultScript =
    [
        new("S01", "zh", "这批支架的料号是 KX-4402，表面处理按上次的标准做。"),
        new("S02", "pl", "Musimy potwierdzić termin dostawy przed końcem sierpnia."),
        new("S03", "de", "Die Toleranzen im Zeichnungssatz sind noch nicht freigegeben."),
        new("S01", "zh", "阳极氧化的颜色样品下周一寄出，顺丰到华沙大概四天。"),
        new("S02", "pl", "Czy próbki będą zgodne z normą ISO 7599?"),
        new("S03", "de", "Wir brauchen außerdem das Erstmusterprüfprotokoll für KX-4402.", CodeSwitch: true),
    ];

    private readonly IReadOnlyList<Line> _script;
    private readonly TimeSpan _partialInterval;
    private readonly bool _loop;

    public FakeAsrProvider(
        IReadOnlyList<Line>? script = null,
        TimeSpan? partialInterval = null,
        bool loop = false,
        AsrCapabilities? caps = null)
    {
        _script = script ?? DefaultScript;
        _partialInterval = partialInterval ?? TimeSpan.FromMilliseconds(350);
        _loop = loop;
        Caps = caps ?? new AsrCapabilities(
            Streaming: true,
            Diarization: true,
            Translation: false,
            AutoLanguageDetect: true,
            Languages: new HashSet<string> { "zh", "de", "pl", "en" },
            Latency: LatencyClass.Realtime);
    }

    public string Id => "fake";

    public AsrCapabilities Caps { get; }

    public Task<IAsrSession> StartAsync(AsrSessionOptions options, CancellationToken ct) =>
        Task.FromResult<IAsrSession>(new Session(_script, _partialInterval, _loop));

    private sealed class Session : IAsrSession
    {
        private readonly CancellationTokenSource _cts = new();
        private readonly Channel<AsrEvent> _events = Channel.CreateUnbounded<AsrEvent>();
        private readonly Task _generator;

        public Session(IReadOnlyList<Line> script, TimeSpan partialInterval, bool loop)
        {
            _generator = Task.Run(() => GenerateAsync(script, partialInterval, loop, _cts.Token));
        }

        public IAsyncEnumerable<AsrEvent> Events => ReadEventsAsync();

        public ValueTask PushAudioAsync(ReadOnlyMemory<byte> pcm16, CancellationToken ct = default) =>
            ValueTask.CompletedTask;

        private async IAsyncEnumerable<AsrEvent> ReadEventsAsync(
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            await foreach (var e in _events.Reader.ReadAllAsync(ct))
                yield return e;
        }

        private async Task GenerateAsync(
            IReadOnlyList<Line> script, TimeSpan partialInterval, bool loop, CancellationToken ct)
        {
            try
            {
                long clockMs = 0;
                var sequence = 0;
                do
                {
                    foreach (var line in script)
                    {
                        var id = $"u{sequence++:D4}";
                        var start = clockMs;
                        var words = line.Text.Length <= 12
                            ? [line.Text]
                            : SplitIntoChunks(line.Text, 4);

                        for (var i = 0; i < words.Count; i++)
                        {
                            await Task.Delay(partialInterval, ct);
                            clockMs += (long)partialInterval.TotalMilliseconds;
                            var isFinal = i == words.Count - 1;
                            var text = string.Concat(words.Take(i + 1));
                            await _events.Writer.WriteAsync(new AsrEvent.Transcript(
                                id, line.SpeakerTag, text, line.Lang,
                                start, isFinal ? clockMs : null, isFinal,
                                line.CodeSwitch, SpeakerConfidence: 0.92, Translations: null), ct);
                        }
                    }
                }
                while (loop && !ct.IsCancellationRequested);

                await _events.Writer.WriteAsync(new AsrEvent.Ended("script finished"), ct);
                _events.Writer.Complete();
            }
            catch (OperationCanceledException)
            {
                _events.Writer.TryComplete();
            }
            catch (Exception ex)
            {
                _events.Writer.TryWrite(new AsrEvent.Error(ex.Message, Fatal: true));
                _events.Writer.TryComplete();
            }
        }

        private static List<string> SplitIntoChunks(string text, int parts)
        {
            var result = new List<string>(parts);
            var size = (text.Length + parts - 1) / parts;
            for (var i = 0; i < text.Length; i += size)
                result.Add(text.Substring(i, Math.Min(size, text.Length - i)));
            return result;
        }

        public async ValueTask DisposeAsync()
        {
            _cts.Cancel();
            try
            {
                await _generator;
            }
            catch (OperationCanceledException)
            {
            }

            _cts.Dispose();
        }
    }
}
