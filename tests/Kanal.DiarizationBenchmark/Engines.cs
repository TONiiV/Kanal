using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using Kanal.Core.Meetings;
using Kanal.Core.Models;
using Kanal.Providers.LocalDiarization;
using SherpaOnnx;

namespace Kanal.DiarizationBenchmark;

[ExcludeFromCodeCoverage]
internal static class Engines
{
    public static int Threads { get; set; } = Math.Max(1, Environment.ProcessorCount / 2);

    public static string ModelPath(string modelsDir, string data, DiarizationModelInfo model)
    {
        var installed = Path.Combine(modelsDir, model.FileName);
        if (File.Exists(installed))
            return installed;
        var downloads = new ModelDownloadManager(Path.Combine(data, "models"));
        if (!downloads.IsDownloaded(model))
        {
            Console.WriteLine($"Downloading {model.DownloadUrl}");
            downloads.DownloadAsync(model, null, CancellationToken.None).GetAwaiter().GetResult();
        }

        return downloads.GetPath(model);
    }

    // The same configuration as SherpaSpeakerDiarizer, plus the threshold it leaves at the library default.
    public static OfflineSpeakerDiarization Load(string segmentation, string embedding, int? clusters, float? threshold)
    {
        var config = new OfflineSpeakerDiarizationConfig();
        config.Segmentation.Pyannote.Model = segmentation;
        config.Segmentation.NumThreads = Threads;
        config.Embedding.Model = embedding;
        config.Embedding.NumThreads = Threads;
        if (clusters is > 0)
            config.Clustering.NumClusters = clusters.Value;
        if (threshold is { } t)
            config.Clustering.Threshold = t;
        return new OfflineSpeakerDiarization(config);
    }

    public static float DefaultThreshold => new OfflineSpeakerDiarizationConfig().Clustering.Threshold;

    public static IReadOnlyList<SpeakerTurn> Diarize(OfflineSpeakerDiarization diarization, float[] samples) =>
        [.. diarization.Process(samples).Select(s => new SpeakerTurn(s.Start, s.End, s.Speaker))];

    public static IReadOnlyList<SpeakerTurn> Product(string segmentation, string embedding, string wav, int? count) =>
        new SherpaSpeakerDiarizer(segmentation, embedding).DiarizeAsync(wav, count, null, CancellationToken.None).GetAwaiter().GetResult();

    public static float[][] Embeddings(string embeddingModel, Meeting meeting)
    {
        using var embedder = new SherpaSpeakerEmbedder(embeddingModel);
        return [.. meeting.Truth.Select(u => Slice(meeting, u))
            .Select(s => s.Length < LiveSpeakerLabels.MinimumSpeech.TotalSeconds * Corpus.Rate ? [] : embedder.Embed(s))];
    }

    public static string?[] Live(Meeting meeting, float[][] embeddings, double threshold)
    {
        using var labels = new LiveSpeakerLabels(new Replay(embeddings), threshold);
        return [.. meeting.Truth.Select(u => labels.Assign(Kanal.Audio.PcmConvert.ShortsToBytes(
            [.. Slice(meeting, u).Select(s => (short)Math.Clamp(s * 32767f, short.MinValue, short.MaxValue))])))];
    }

    private static float[] Slice(Meeting meeting, Segment u)
    {
        var from = (int)Math.Round(u.Start * Corpus.Rate);
        var to = Math.Min(meeting.Samples.Length, (int)Math.Round(u.End * Corpus.Rate));
        return meeting.Samples[from..to];
    }

    // Replays the embeddings in utterance order; LiveSpeakerLabels skips utterances under 1 s without asking.
    private sealed class Replay(float[][] embeddings) : ISpeakerEmbedder
    {
        private int _next;

        public float[] Embed(float[] samples)
        {
            while (embeddings[_next].Length == 0)
                _next++;
            return embeddings[_next++];
        }

        public void Dispose()
        {
        }
    }
}

[ExcludeFromCodeCoverage]
internal sealed class PeakMemory : IDisposable
{
    private readonly Timer _timer;
    private long _peak = Environment.WorkingSet;

    public PeakMemory() => _timer = new Timer(_ => Interlocked.Exchange(ref _peak, Math.Max(Interlocked.Read(ref _peak), Environment.WorkingSet)), null, 0, 50);

    public double PeakMb => Interlocked.Read(ref _peak) / 1048576.0;

    public void Dispose() => _timer.Dispose();
}

[ExcludeFromCodeCoverage]
internal static class Clock
{
    public static (T Value, double Seconds) Time<T>(Func<T> run)
    {
        var watch = Stopwatch.StartNew();
        var value = run();
        return (value, watch.Elapsed.TotalSeconds);
    }
}
