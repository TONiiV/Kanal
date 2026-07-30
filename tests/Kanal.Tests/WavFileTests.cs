using Kanal.Audio;

namespace Kanal.Tests;

public class WavFileTests
{
    [Fact]
    public void RoundTripsPcm16()
    {
        var pcm = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 };
        using var stream = new MemoryStream();

        WavFile.Write(stream, pcm, 16_000, 1);
        stream.Position = 0;
        var read = WavFile.Read(stream);

        Assert.Equal(16_000, read.SampleRateHz);
        Assert.Equal(1, read.Channels);
        Assert.Equal(pcm, read.Pcm16);
    }
}
