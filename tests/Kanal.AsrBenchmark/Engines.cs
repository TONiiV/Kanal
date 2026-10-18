using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Threading.Channels;
using Kanal.Core.Models;
using Kanal.Core.Providers;
using Kanal.Providers.Gladia;
using Kanal.Providers.LocalAsr;
using SherpaOnnx;

namespace Kanal.AsrBenchmark;

[ExcludeFromCodeCoverage]
internal sealed record BenchModel(
    string Id,
    string Kind,
    double ChunkSeconds,
    IReadOnlyDictionary<string, string> Files);

[ExcludeFromCodeCoverage]
internal abstract class Engine : IDisposable
{
    public virtual double PaddingSeconds => 0;

    public virtual bool Cloud => false;

    public virtual bool NeedsLanguage => false;

    public virtual IAsrProvider Provider(List<double> decodeMs) => new NemotronAsrProvider(lang => Open(lang, decodeMs));

    public abstract IRecognizerStream Open(string language, List<double> decodeMs);

    public abstract void Dispose();

    public static string Key(string name)
    {
        var key = Environment.GetEnvironmentVariable(name)
                  ?? (OperatingSystem.IsWindows()
                      ? Environment.GetEnvironmentVariable(name, EnvironmentVariableTarget.User)
                        ?? Environment.GetEnvironmentVariable(name, EnvironmentVariableTarget.Machine)
                      : null);
        return string.IsNullOrWhiteSpace(key) ? throw new InvalidOperationException($"{name} is not set.") : key.Trim();
    }

    public static int DefaultThreads => Math.Clamp(Environment.ProcessorCount / 2, 1, 4);

    public static BenchModel Default(string modelsDir)
    {
        var model = AsrModelCatalog.Models[0];
        var downloads = new ModelDownloadManager(modelsDir);
        return new BenchModel(model.Id, "nemotron", 0.56,
            model.Parts.ToDictionary(p => p.RemoteName, p => downloads.GetPath(p)));
    }

    public static IReadOnlyList<(string Id, string Kind, double Chunk, string Repo, string[] Files)> Candidates { get; } =
    [
        ("nemotron-3.5-asr-160ms-int8", "nemotron", 0.16,
            "csukuangfj2/sherpa-onnx-nemotron-3.5-asr-streaming-0.6b-160ms-int8-2026-06-11",
            ["encoder.int8.onnx", "decoder.int8.onnx", "joiner.int8.onnx", "tokens.txt"]),
        ("whisper-turbo-int8", "whisper", 0, "csukuangfj/sherpa-onnx-whisper-turbo",
            ["turbo-encoder.int8.onnx", "turbo-decoder.int8.onnx", "turbo-tokens.txt"]),
        ("qwen3-asr-0.6b-int8", "qwen3", 0, "csukuangfj2/sherpa-onnx-qwen3-asr-0.6B-int8-2026-03-25",
            ["conv_frontend.onnx", "encoder.int8.onnx", "decoder.int8.onnx",
             "tokenizer/merges.txt", "tokenizer/tokenizer_config.json", "tokenizer/vocab.json"]),
        ("qwen3-asr-1.7b-int8", "qwen3", 0, "thieunv-asilla/sherpa-onnx-qwen3-asr-1.7B-int8@69eb686fd94a4a865bb5340a3d6ac0d7f1fec0d5",
            ["conv_frontend.onnx", "encoder.int8.onnx", "decoder.int8.onnx",
             "tokenizer/merges.txt", "tokenizer/tokenizer_config.json", "tokenizer/vocab.json"]),
        ("whisper-large-v3-int8", "whisper", 0, "csukuangfj/sherpa-onnx-whisper-large-v3",
            ["large-v3-encoder.int8.onnx", "large-v3-decoder.int8.onnx", "large-v3-tokens.txt"]),
        ("cohere-transcribe-int8", "cohere", 0, "csukuangfj2/sherpa-onnx-cohere-transcribe-14-lang-int8-2026-04-01",
            ["encoder.int8.onnx", "encoder.int8.onnx.data", "decoder.int8.onnx", "tokens.txt"]),
    ];

    private const string VadUrl = "https://github.com/k2-fsa/sherpa-onnx/releases/download/asr-models/silero_vad.onnx";

    public static IEnumerable<BenchModel> Downloaded(string data) =>
        Candidates
            .Select(c => new BenchModel(c.Id, c.Kind, c.Chunk,
                c.Files.ToDictionary(f => f, f => Path.Combine(data, "models", c.Id, f))))
            .Where(m => m.Files.Values.All(File.Exists));

    public static async Task FetchCandidatesAsync(string data, IReadOnlyCollection<string> ids)
    {
        using var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        await DownloadAsync(http, VadUrl, Path.Combine(data, "models", "silero_vad.onnx"));
        foreach (var (id, _, _, repo, files) in Candidates.Where(c => ids.Contains(c.Id) || ids.Contains("all")))
        foreach (var file in files)
            await DownloadAsync(http, $"https://huggingface.co/{(repo.Contains('@') ? repo.Replace("@", "/resolve/") : repo + "/resolve/main")}/{file}",
                Path.Combine(data, "models", id, file));
    }

    private static async Task DownloadAsync(HttpClient http, string url, string path)
    {
        if (File.Exists(path))
            return;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        Console.WriteLine($"Downloading {url}");
        await using (var file = File.Create(path + ".part"))
        await using (var body = await http.GetStreamAsync(url))
            await body.CopyToAsync(file);
        File.Move(path + ".part", path, overwrite: true);
    }

    public static BenchModel Gladia { get; } = new("gladia", "gladia", 0, new Dictionary<string, string>());

    public static Engine Load(BenchModel model, string provider, int threads, string data) => model.Kind switch
    {
        "nemotron" => new NemotronEngine(model, provider, threads),
        "gladia" => new GladiaEngine(),
        _ => new OfflineEngine(model, provider, threads, Path.Combine(data, "models", "silero_vad.onnx")),
    };
}

// Offline models get Silero VAD segments; a segment's text is a final, and there are no partials.
[ExcludeFromCodeCoverage]
internal sealed class OfflineEngine : Engine
{
    public const float MinSilenceSeconds = 0.5f;

    private readonly BenchModel _model;
    private OfflineRecognizerConfig _config;
    private readonly OfflineRecognizer _recognizer;
    private readonly VadModelConfig _vad;
    private readonly Lock _gate = new();
    private string _language = "";

    public OfflineEngine(BenchModel model, string provider, int threads, string vadModel)
    {
        _model = model;
        var files = model.Files;
        _config = new OfflineRecognizerConfig();
        _config.FeatConfig.SampleRate = Corpus.Rate;
        _config.ModelConfig.NumThreads = threads;
        _config.ModelConfig.Provider = provider;
        if (model.Kind == "whisper")
        {
            _config.ModelConfig.Whisper.Encoder = files.Values.Single(f => f.EndsWith("encoder.int8.onnx"));
            _config.ModelConfig.Whisper.Decoder = files.Values.Single(f => f.EndsWith("decoder.int8.onnx"));
            _config.ModelConfig.Tokens = files.Values.Single(f => f.EndsWith("tokens.txt"));
        }
        else if (model.Kind == "cohere")
        {
            _config.ModelConfig.CohereTranscribe.Encoder = files["encoder.int8.onnx"];
            _config.ModelConfig.CohereTranscribe.Decoder = files["decoder.int8.onnx"];
            _config.ModelConfig.Tokens = files["tokens.txt"];
        }
        else
        {
            _config.ModelConfig.Qwen3Asr.ConvFrontend = files["conv_frontend.onnx"];
            _config.ModelConfig.Qwen3Asr.Encoder = files["encoder.int8.onnx"];
            _config.ModelConfig.Qwen3Asr.Decoder = files["decoder.int8.onnx"];
            _config.ModelConfig.Qwen3Asr.Tokenizer = Path.GetDirectoryName(files["tokenizer/vocab.json"])!;
            _config.ModelConfig.Qwen3Asr.MaxNewTokens = 256;
        }

        _recognizer = new OfflineRecognizer(_config);
        _vad = new VadModelConfig { SampleRate = Corpus.Rate, NumThreads = 1, Provider = "cpu" };
        _vad.SileroVad.Model = vadModel;
        _vad.SileroVad.MinSilenceDuration = MinSilenceSeconds;
        _vad.SileroVad.MaxSpeechDuration = 20;
    }

    public override bool NeedsLanguage => _model.Kind == "cohere";

    public override IRecognizerStream Open(string language, List<double> decodeMs) => new Stream(this, language, decodeMs);

    public override void Dispose() => _recognizer.Dispose();

    private string Recognize(float[] samples, string language)
    {
        using var stream = _recognizer.CreateStream();
        if (_model.Kind == "cohere")
        {
            stream.SetOption("language", language[..2]);
        }
        else if (_model.Kind == "whisper")
        {
            // Whisper reads the language from the recognizer config, not from the stream.
            var code = language == "auto" ? "" : language[..2];
            lock (_gate)
                if (_language != code)
                {
                    _config.ModelConfig.Whisper.Language = code;
                    _recognizer.SetConfig(_config);
                    _language = code;
                }
        }
        else if (language != "auto")
        {
            stream.SetOption("language", language[..2] switch { "zh" => "Chinese", "de" => "German", "pl" => "Polish", "en" => "English", var l => l });
        }

        stream.AcceptWaveform(Corpus.Rate, samples);
        _recognizer.Decode(stream);
        var text = stream.Result.Text;
        return Scoring.StripAsrHeader(text);
    }

    [ExcludeFromCodeCoverage]
    private sealed class Stream(OfflineEngine engine, string language, List<double> decodeMs) : IRecognizerStream
    {
        private readonly VoiceActivityDetector _vad = new(engine._vad, 60);
        private string _text = "";
        private bool _endpoint;

        public void Accept(float[] samples) => _vad.AcceptWaveform(samples);

        public string Decode()
        {
            while (!_vad.IsEmpty())
            {
                var start = Stopwatch.GetTimestamp();
                var text = engine.Recognize(_vad.Front().Samples, language);
                decodeMs.Add(Stopwatch.GetElapsedTime(start).TotalMilliseconds);
                _vad.Pop();
                _text = (_text + " " + text).Trim();
                _endpoint = true;
            }

            return _text;
        }

        public bool IsEndpoint => _endpoint;

        public void Reset()
        {
            _text = "";
            _endpoint = false;
        }

        public void Finish() => _vad.Flush();

        public void Dispose() => _vad.Dispose();
    }
}

// Mirrors NemotronAsrProvider.Load, with the thread count and execution provider open.
[ExcludeFromCodeCoverage]
internal sealed class NemotronEngine : Engine
{
    public override double PaddingSeconds => 2;

    private readonly OnlineRecognizer _recognizer;

    public NemotronEngine(BenchModel model, string provider, int threads)
    {
        foreach (var path in model.Files.Values)
            if (!File.Exists(path))
                throw new FileNotFoundException("Model file missing.", path);

        var config = new OnlineRecognizerConfig();
        config.FeatConfig.SampleRate = Corpus.Rate;
        config.FeatConfig.FeatureDim = 128;
        config.ModelConfig.Transducer.Encoder = model.Files["encoder.int8.onnx"];
        config.ModelConfig.Transducer.Decoder = model.Files["decoder.int8.onnx"];
        config.ModelConfig.Transducer.Joiner = model.Files["joiner.int8.onnx"];
        config.ModelConfig.Tokens = model.Files["tokens.txt"];
        config.ModelConfig.NumThreads = threads;
        config.ModelConfig.Provider = provider;
        config.EnableEndpoint = 1;
        config.Rule1MinTrailingSilence = 2.4f;
        config.Rule2MinTrailingSilence = 1.6f;
        config.Rule3MinUtteranceLength = 20f;
        _recognizer = new OnlineRecognizer(config);
    }

    public override IRecognizerStream Open(string language, List<double> decodeMs) =>
        new Stream(_recognizer, language, decodeMs);

    public override void Dispose() => _recognizer.Dispose();

    // Mirrors NemotronAsrProvider.SherpaStream and times every Decode that ran the model.
    [ExcludeFromCodeCoverage]
    private sealed class Stream : IRecognizerStream
    {
        private readonly OnlineRecognizer _recognizer;
        private readonly OnlineStream _stream;
        private readonly List<double> _decodeMs;

        public Stream(OnlineRecognizer recognizer, string language, List<double> decodeMs)
        {
            _recognizer = recognizer;
            _decodeMs = decodeMs;
            _stream = recognizer.CreateStream();
            _stream.SetOption("language", language);
            _stream.AcceptWaveform(Corpus.Rate, new float[Corpus.Rate]);
        }

        public void Accept(float[] samples) => _stream.AcceptWaveform(Corpus.Rate, samples);

        public string Decode()
        {
            var start = Stopwatch.GetTimestamp();
            var ran = false;
            while (_recognizer.IsReady(_stream))
            {
                _recognizer.Decode(_stream);
                ran = true;
            }

            if (ran)
                _decodeMs.Add(Stopwatch.GetElapsedTime(start).TotalMilliseconds);
            return _recognizer.GetResult(_stream).Text;
        }

        public bool IsEndpoint => _recognizer.IsEndpoint(_stream);

        public void Reset() => _recognizer.Reset(_stream);

        public void Finish()
        {
            _stream.AcceptWaveform(Corpus.Rate, new float[Corpus.Rate]);
            _stream.InputFinished();
        }

        public void Dispose() => _stream.Dispose();
    }
}

// The product sends no room languages to Gladia (GladiaAsrProvider.BuildInitBody), so every room is the auto room.
[ExcludeFromCodeCoverage]
internal sealed class GladiaEngine : Engine
{
    private readonly GladiaAsrProvider _provider;

    public GladiaEngine()
    {
        _provider = new GladiaAsrProvider(new GladiaOptions { ApiKey = Key("GLADIA_API_KEY"), EnableTranslation = false });
    }

    public override bool Cloud => true;

    public override double PaddingSeconds => 1;

    public override IAsrProvider Provider(List<double> decodeMs) => new Settled(_provider);

    public override IRecognizerStream Open(string language, List<double> decodeMs) => throw new NotSupportedException();

    public override void Dispose() => _provider.Dispose();

    // GladiaAsrSession.DisposeAsync closes the socket right after stop_recording, so this waits for the last final first.
    [ExcludeFromCodeCoverage]
    private sealed class Settled(GladiaAsrProvider inner) : IAsrProvider
    {
        public string Id => inner.Id;

        public AsrCapabilities Caps => inner.Caps;

        public async Task<IAsrSession> StartAsync(AsrSessionOptions options, CancellationToken ct) =>
            new Session(await inner.StartAsync(options, ct));
    }

    [ExcludeFromCodeCoverage]
    private sealed class Session : IAsrSession
    {
        private readonly IAsrSession _inner;
        private readonly Channel<AsrEvent> _events = Channel.CreateUnbounded<AsrEvent>();
        private readonly Task _pump;
        private long _last = Stopwatch.GetTimestamp();
        private volatile bool _partial;

        public Session(IAsrSession inner)
        {
            _inner = inner;
            _pump = Task.Run(async () =>
            {
                await foreach (var e in inner.Events)
                {
                    if (e is AsrEvent.Transcript t)
                    {
                        _partial = !t.IsFinal;
                        Interlocked.Exchange(ref _last, Stopwatch.GetTimestamp());
                    }

                    _events.Writer.TryWrite(e);
                }

                _events.Writer.TryComplete();
            });
        }

        public IAsyncEnumerable<AsrEvent> Events => _events.Reader.ReadAllAsync();

        public ValueTask PushAudioAsync(ReadOnlyMemory<byte> pcm16, CancellationToken ct = default) => _inner.PushAudioAsync(pcm16, ct);

        public async ValueTask DisposeAsync()
        {
            var deadline = Stopwatch.GetTimestamp() + Stopwatch.Frequency * 10;
            while (Stopwatch.GetTimestamp() < deadline
                   && (_partial || Stopwatch.GetElapsedTime(Interlocked.Read(ref _last)) < TimeSpan.FromSeconds(1.5)))
                await Task.Delay(100);
            try
            {
                await _inner.DisposeAsync();
            }
            catch (OperationCanceledException)
            {
            }

            // A close during the reconnect backoff leaves the inner event stream open for good.
            _events.Writer.TryComplete();
            await Task.WhenAny(_pump, Task.Delay(TimeSpan.FromSeconds(2)));
        }
    }
}
