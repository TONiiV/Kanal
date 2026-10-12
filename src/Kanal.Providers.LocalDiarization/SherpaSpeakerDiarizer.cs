using System.Runtime.InteropServices;
using System.Text;
using Kanal.Audio;
using Kanal.Core.Meetings;
using SherpaOnnx;

namespace Kanal.Providers.LocalDiarization;

public sealed class SherpaSpeakerDiarizer(string segmentationModel, string embeddingModel) : ISpeakerDiarizer
{
    public Task<IReadOnlyList<SpeakerTurn>> DiarizeAsync(
        string wavPath, int? speakerCount, IProgress<double>? progress, CancellationToken ct) =>
        Task.Run(() => Diarize(wavPath, speakerCount, progress), ct);

    private IReadOnlyList<SpeakerTurn> Diarize(string wavPath, int? speakerCount, IProgress<double>? progress)
    {
        CheckModelPath(segmentationModel, OperatingSystem.IsWindows());
        CheckModelPath(embeddingModel, OperatingSystem.IsWindows());
        var samples = ReadSamples(wavPath);
        var threads = Math.Max(1, Environment.ProcessorCount / 2);

        var config = new OfflineSpeakerDiarizationConfig();
        config.Segmentation.Pyannote.Model = segmentationModel;
        config.Segmentation.NumThreads = threads;
        config.Embedding.Model = embeddingModel;
        config.Embedding.NumThreads = threads;
        if (speakerCount is > 0)
            config.Clustering.NumClusters = speakerCount.Value;

        using var diarization = new OfflineSpeakerDiarization(config);
        OfflineSpeakerDiarizationProgressCallback callback = (done, total, _) =>
        {
            if (total > 0)
                progress?.Report((double)done / total);
            return 0;
        };
        var segments = diarization.ProcessWithCallback(samples, callback, IntPtr.Zero);
        // Native code holds the delegate for the whole call; a collected delegate crashes the host.
        GC.KeepAlive(callback);

        return [.. segments.Select(s => new SpeakerTurn(s.Start, s.End, s.Speaker))];
    }

    // sherpa-onnx aborts the process on a model it cannot open, and 1.13.8 never checks the handle.
    // It marshals the path as LPStr: the ANSI code page on Windows, which cannot hold every user name.
    internal static void CheckModelPath(string path, bool isWindows)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException($"The speaker model {path} is missing.", path);
        if (isWindows && !Ascii.IsValid(path))
            throw new IOException(
                $"The speaker model path {path} has non-ASCII characters, which the speaker library cannot open on Windows.");
    }

    // ponytail: the whole recording as one float[] (~230 MB per hour); chunk it if meetings run many hours.
    private static float[] ReadSamples(string wavPath)
    {
        WavFile.WavData wav;
        using (var file = File.OpenRead(wavPath))
            wav = WavFile.Read(file);

        if (wav.SampleRateHz != MeetingTimeline.SampleRateHz || wav.Channels != 1)
            throw new InvalidDataException(
                $"{wavPath} is {wav.SampleRateHz} Hz, {wav.Channels} channel(s); diarization needs 16 kHz mono.");

        var pcm = MemoryMarshal.Cast<byte, short>(wav.Pcm16);
        var samples = new float[pcm.Length];
        for (var i = 0; i < pcm.Length; i++)
            samples[i] = pcm[i] / 32768f;
        return samples;
    }
}
