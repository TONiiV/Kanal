using System.Diagnostics;
using Kanal.Core.Models;
using Kanal.Providers.LocalAsr;
using SherpaOnnx;

namespace Kanal.AsrBenchmark;

internal sealed record BenchModel(
    string Id,
    string Kind,
    double ChunkSeconds,
    IReadOnlyDictionary<string, string> Files);

internal abstract class Engine : IDisposable
{
    public abstract IRecognizerStream Open(string language, List<double> decodeMs);

    public abstract void Dispose();

    public static int DefaultThreads => Math.Clamp(Environment.ProcessorCount / 2, 1, 4);

    public static BenchModel Default(string modelsDir)
    {
        var model = AsrModelCatalog.Models[0];
        var downloads = new ModelDownloadManager(modelsDir);
        return new BenchModel(model.Id, "nemotron", 0.56,
            model.Parts.ToDictionary(p => p.RemoteName, p => downloads.GetPath(p)));
    }

    public static Engine Load(BenchModel model, string provider, int threads) => model.Kind switch
    {
        "nemotron" => new NemotronEngine(model, provider, threads),
        _ => throw new NotSupportedException(model.Kind),
    };
}

// Mirrors NemotronAsrProvider.Load, with the thread count and execution provider open.
internal sealed class NemotronEngine : Engine
{
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
