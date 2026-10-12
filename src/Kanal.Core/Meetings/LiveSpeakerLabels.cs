using System.Globalization;
using System.Runtime.InteropServices;

namespace Kanal.Core.Meetings;

public interface ISpeakerEmbedder : IDisposable
{
    float[] Embed(float[] samples);
}

// ADR 0055 slice 4. The centroids live only in this object, so they end with the meeting: a
// voiceprint kept across meetings is biometric data that needs its own consent design.
public sealed class LiveSpeakerLabels(ISpeakerEmbedder embedder, double threshold = LiveSpeakerLabels.DefaultThreshold)
    : IDisposable
{
    // Unmeasured on zh/de/pl; slice 6 (#111) calibrates it.
    public const double DefaultThreshold = 0.5;

    public static readonly TimeSpan MinimumSpeech = TimeSpan.FromSeconds(1);

    private readonly List<(string Tag, float[] Sum)> _speakers = [];
    private string? _previous;

    public string? Assign(ReadOnlySpan<byte> pcm16)
    {
        if (pcm16.Length < MinimumSpeech.TotalSeconds * MeetingTimeline.BytesPerSecond)
            return _previous;

        var embedding = Normalized(embedder.Embed(ToSamples(pcm16)));
        var best = -1;
        var bestScore = double.MinValue;
        for (var i = 0; i < _speakers.Count; i++)
        {
            var score = Cosine(embedding, _speakers[i].Sum);
            if (score > bestScore)
                (best, bestScore) = (i, score);
        }

        if (best >= 0 && bestScore >= threshold)
        {
            var sum = _speakers[best].Sum;
            for (var i = 0; i < sum.Length; i++)
                sum[i] += embedding[i];
            _previous = _speakers[best].Tag;
        }
        else
        {
            _previous = string.Create(CultureInfo.InvariantCulture, $"V{_speakers.Count + 1:00}?");
            _speakers.Add((_previous, embedding));
        }

        return _previous;
    }

    public void Dispose() => embedder.Dispose();

    private static float[] ToSamples(ReadOnlySpan<byte> pcm16)
    {
        var pcm = MemoryMarshal.Cast<byte, short>(pcm16);
        var samples = new float[pcm.Length];
        for (var i = 0; i < pcm.Length; i++)
            samples[i] = pcm[i] / 32768f;
        return samples;
    }

    private static float[] Normalized(float[] v)
    {
        var norm = Math.Sqrt(v.Sum(x => (double)x * x));
        return norm == 0 ? v : [.. v.Select(x => (float)(x / norm))];
    }

    private static double Cosine(float[] unit, float[] sum)
    {
        double dot = 0, norm = 0;
        for (var i = 0; i < sum.Length; i++)
        {
            dot += unit[i] * sum[i];
            norm += sum[i] * sum[i];
        }

        return norm == 0 ? 0 : dot / Math.Sqrt(norm);
    }
}
