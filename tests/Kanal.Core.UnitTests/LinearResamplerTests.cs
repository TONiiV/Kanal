using Kanal.Audio;

namespace Kanal.Core.UnitTests;

public class LinearResamplerTests
{
    [Fact]
    public void Downsamples48kTo16kAtOneThirdLength()
    {
        var resampler = new LinearResampler(48_000, 16_000);
        var input = new short[48_000]; // 1 s
        var output = new short[resampler.GetMaxOutputCount(input.Length)];

        var count = resampler.Resample(input, output);

        Assert.InRange(count, 15_990, 16_010);
    }

    [Fact]
    public void PreservesDcSignal()
    {
        var resampler = new LinearResampler(44_100, 16_000);
        var input = new short[44_100];
        Array.Fill(input, (short)1000);
        var output = new short[resampler.GetMaxOutputCount(input.Length)];

        var count = resampler.Resample(input, output);

        Assert.True(count > 0);
        Assert.All(output[..count], s => Assert.Equal(1000, s));
    }

    [Fact]
    public void ChunkedProcessingMatchesSinglePass()
    {
        var rng = new Random(42);
        var input = new short[10_000];
        for (var i = 0; i < input.Length; i++)
            input[i] = (short)rng.Next(short.MinValue, short.MaxValue);

        var single = new LinearResampler(48_000, 16_000);
        var singleOut = new short[single.GetMaxOutputCount(input.Length)];
        var singleCount = single.Resample(input, singleOut);

        var chunked = new LinearResampler(48_000, 16_000);
        var chunkedOut = new List<short>();
        var buffer = new short[chunked.GetMaxOutputCount(input.Length)];
        var offset = 0;
        var sizes = new[] { 137, 1024, 3, 999, 4096 };
        var sizeIndex = 0;
        while (offset < input.Length)
        {
            var size = Math.Min(sizes[sizeIndex++ % sizes.Length], input.Length - offset);
            var count = chunked.Resample(input.AsSpan(offset, size), buffer);
            chunkedOut.AddRange(buffer[..count]);
            offset += size;
        }

        Assert.Equal(singleOut[..singleCount], chunkedOut.ToArray());
    }

    [Fact]
    public void UpsamplingProducesMoreSamples()
    {
        var resampler = new LinearResampler(8_000, 16_000);
        var input = new short[800];
        var output = new short[resampler.GetMaxOutputCount(input.Length)];

        var count = resampler.Resample(input, output);

        Assert.InRange(count, 1_590, 1_610);
    }
}
