using Kanal.Core.Meetings;
using SherpaOnnx;

namespace Kanal.Providers.LocalDiarization;

public sealed class SherpaSpeakerEmbedder(string embeddingModel) : ISpeakerEmbedder
{
    // Loaded on the first sentence, on the labelling thread: the constructor runs on the UI thread at Start.
    private readonly Lazy<SpeakerEmbeddingExtractor> _extractor = new(() =>
    {
        var config = new SpeakerEmbeddingExtractorConfig();
        config.Model = embeddingModel;
        config.NumThreads = 1;
        return new SpeakerEmbeddingExtractor(config);
    });

    public float[] Embed(float[] samples)
    {
        var extractor = _extractor.Value;
        using var stream = extractor.CreateStream();
        stream.AcceptWaveform(MeetingTimeline.SampleRateHz, samples);
        stream.InputFinished();
        return extractor.IsReady(stream)
            ? extractor.Compute(stream)
            : throw new InvalidOperationException($"{samples.Length} samples are too few for a speaker embedding.");
    }

    public void Dispose()
    {
        if (_extractor.IsValueCreated)
            _extractor.Value.Dispose();
    }
}
