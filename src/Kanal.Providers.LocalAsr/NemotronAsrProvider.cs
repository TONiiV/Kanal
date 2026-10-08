using System.Threading.Channels;
using Kanal.Core.Models;
using Kanal.Core.Providers;
using SherpaOnnx;

namespace Kanal.Providers.LocalAsr;

public interface IRecognizerStream : IDisposable
{
    void Accept(float[] samples);

    string Decode();

    bool IsEndpoint { get; }

    void Reset();

    void Finish();
}

public sealed class NemotronAsrProvider : IAsrProvider, IWarmupProvider, IDisposable
{
    private const int SampleRate = 16_000;

    private readonly Func<string, IRecognizerStream> _streams;
    private readonly Func<OnlineRecognizer>? _load;
    private readonly Lock _gate = new();
    private OnlineRecognizer? _recognizer;

    public NemotronAsrProvider(AsrModelInfo model, ModelDownloadManager downloads)
    {
        _load = () => Load(model, downloads);
        _streams = language => new SherpaStream(Recognizer(), language);
    }

    public NemotronAsrProvider(Func<string, IRecognizerStream> streams) => _streams = streams;

    public string Id => "nemotron";

    public AsrCapabilities Caps { get; } = new(
        Streaming: true,
        Diarization: false,
        Translation: false,
        // "auto" decodes any room language, but the model never reports which; TranscriptLanguage guesses it.
        AutoLanguageDetect: false,
        Languages: new HashSet<string>
        {
            "ar", "bg", "cs", "da", "de", "el", "en", "es", "et", "fi", "fr", "hi", "hr", "hu", "it", "ja",
            "ko", "lt", "lv", "nb", "nl", "nn", "no", "pl", "pt", "ro", "ru", "sk", "sl", "sv", "tr", "uk", "zh",
        },
        Latency: LatencyClass.Near);

    public Task WarmUpAsync(CancellationToken ct) => _load is null ? Task.CompletedTask : Task.Run(Recognizer, ct);

    public Task<IAsrSession> StartAsync(AsrSessionOptions options, CancellationToken ct)
    {
        var room = options.TargetLanguages;
        var forced = room.Count == 1 ? room[0] : null;
        var stream = _streams(forced is null ? "auto" : PromptLanguage(forced));
        return Task.FromResult<IAsrSession>(new Session(stream, room, forced));
    }

    // The model's prompt dictionary has no bare "zh" or "ja"; an unknown key silently decodes as auto.
    private static string PromptLanguage(string code) => code switch
    {
        "zh" => "zh-CN",
        "ja" => "ja-JP",
        _ => code,
    };

    private OnlineRecognizer Recognizer()
    {
        lock (_gate)
            return _recognizer ??= _load!();
    }

    private static OnlineRecognizer Load(AsrModelInfo model, ModelDownloadManager downloads)
    {
        string PathOf(string remote)
        {
            var path = downloads.GetPath(model.Parts.Single(p => p.RemoteName == remote));
            // sherpa-onnx aborts the process on a missing model file rather than returning an error
            return File.Exists(path) ? path : throw new FileNotFoundException("Model file missing.", path);
        }

        var config = new OnlineRecognizerConfig();
        config.FeatConfig.SampleRate = SampleRate;
        config.FeatConfig.FeatureDim = 128;
        config.ModelConfig.Transducer.Encoder = PathOf("encoder.int8.onnx");
        config.ModelConfig.Transducer.Decoder = PathOf("decoder.int8.onnx");
        config.ModelConfig.Transducer.Joiner = PathOf("joiner.int8.onnx");
        config.ModelConfig.Tokens = PathOf("tokens.txt");
        config.ModelConfig.NumThreads = Math.Clamp(Environment.ProcessorCount / 2, 1, 4);
        config.EnableEndpoint = 1;
        // 1.2 s after speech cut "KX-4402" in half mid-number on a German test sentence; 1.6 s did not.
        config.Rule1MinTrailingSilence = 2.4f;
        config.Rule2MinTrailingSilence = 1.6f;
        config.Rule3MinUtteranceLength = 20f;
        return new OnlineRecognizer(config);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _recognizer?.Dispose();
            _recognizer = null;
        }
    }

    private sealed class SherpaStream : IRecognizerStream
    {
        private readonly OnlineRecognizer _recognizer;
        private readonly OnlineStream _stream;

        public SherpaStream(OnlineRecognizer recognizer, string language)
        {
            _recognizer = recognizer;
            _stream = recognizer.CreateStream();
            _stream.SetOption("language", language);
            // The model drops speech in a stream's first second. Not repeated after Reset: there the
            // padding counts as trailing silence and fires the next endpoint mid-sentence.
            _stream.AcceptWaveform(SampleRate, new float[SampleRate]);
        }

        public void Accept(float[] samples) => _stream.AcceptWaveform(SampleRate, samples);

        public string Decode()
        {
            while (_recognizer.IsReady(_stream))
                _recognizer.Decode(_stream);
            return _recognizer.GetResult(_stream).Text;
        }

        public bool IsEndpoint => _recognizer.IsEndpoint(_stream);

        public void Reset() => _recognizer.Reset(_stream);

        public void Finish()
        {
            // the last chunk only decodes once the encoder has its right context
            _stream.AcceptWaveform(SampleRate, new float[SampleRate]);
            _stream.InputFinished();
        }

        public void Dispose() => _stream.Dispose();
    }

    private sealed class Session : IAsrSession
    {
        private const string Speaker = "S01";

        private readonly IRecognizerStream _stream;
        private readonly IReadOnlyList<string> _room;
        private readonly string? _forced;
        private readonly string _prefix = Guid.NewGuid().ToString("N")[..6];
        private readonly Channel<float[]> _audio = Channel.CreateUnbounded<float[]>(new() { SingleReader = true });
        private readonly Channel<AsrEvent> _events = Channel.CreateUnbounded<AsrEvent>();
        private readonly Task _worker;
        private long _heard;
        private long _start;
        private int _utterance;

        public Session(IRecognizerStream stream, IReadOnlyList<string> room, string? forced)
        {
            _stream = stream;
            _room = room;
            _forced = forced;
            _worker = Task.Run(RunAsync);
        }

        public IAsyncEnumerable<AsrEvent> Events => _events.Reader.ReadAllAsync();

        public ValueTask PushAudioAsync(ReadOnlyMemory<byte> pcm16, CancellationToken ct = default)
        {
            var bytes = pcm16.Span;
            var samples = new float[bytes.Length / 2];
            for (var i = 0; i < samples.Length; i++)
                samples[i] = (short)(bytes[2 * i] | bytes[2 * i + 1] << 8) / 32768f;
            _audio.Writer.TryWrite(samples);
            return ValueTask.CompletedTask;
        }

        private async Task RunAsync()
        {
            try
            {
                var last = "";
                await foreach (var samples in _audio.Reader.ReadAllAsync())
                {
                    _stream.Accept(samples);
                    _heard += samples.Length;
                    var text = _stream.Decode().Trim();
                    if (!_stream.IsEndpoint)
                    {
                        if (text.Length > 0 && text != last)
                            Emit(text, isFinal: false);
                        last = text;
                        continue;
                    }

                    if (text.Length > 0)
                        Emit(text, isFinal: true);
                    _stream.Reset();
                    _start = _heard;
                    last = "";
                }

                _stream.Finish();
                var rest = _stream.Decode().Trim();
                if (rest.Length > 0)
                    Emit(rest, isFinal: true);
                _events.Writer.TryWrite(new AsrEvent.Ended(null));
            }
            catch (Exception ex)
            {
                _events.Writer.TryWrite(new AsrEvent.Error(ex.Message, Fatal: true));
            }
            finally
            {
                _events.Writer.TryComplete();
            }
        }

        private void Emit(string text, bool isFinal)
        {
            _events.Writer.TryWrite(new AsrEvent.Transcript(
                $"{_prefix}-{_utterance:D4}",
                Speaker,
                text,
                _forced ?? TranscriptLanguage.Guess(text, _room),
                _start * 1000 / SampleRate,
                isFinal ? _heard * 1000 / SampleRate : null,
                isFinal,
                CodeSwitch: false,
                SpeakerConfidence: 1.0,
                Translations: null));
            if (isFinal)
                _utterance++;
        }

        public async ValueTask DisposeAsync()
        {
            _audio.Writer.TryComplete();
            await _worker;
            _stream.Dispose();
        }
    }
}
