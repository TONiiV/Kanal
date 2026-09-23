using Kanal.Audio;
using NAudio.Wave;

namespace Kanal.Core.UnitTests;

public class AudioConversionTests
{
    [Fact]
    public void StereoPcm16AveragesToMono()
    {
        short[] interleaved = [100, 300, -1000, 2000, short.MaxValue, short.MaxValue];

        var mono = PcmConvert.DownmixToMono(interleaved, 2);

        Assert.Equal<short[]>([200, 500, short.MaxValue], mono);
    }

    [Fact]
    public void MonoPcm16IsPassedThroughUnchanged()
    {
        short[] samples = [1, -1, 32000];

        Assert.Equal<short[]>(samples, PcmConvert.DownmixToMono(samples, 1));
    }

    [Fact]
    public void Float32SpansTheFullPcm16Range()
    {
        var floats = new[] { 0f, 1f, -1f, 0.5f };
        var bytes = new byte[floats.Length * sizeof(float)];
        Buffer.BlockCopy(floats, 0, bytes, 0, bytes.Length);

        var mono = PcmConvert.Float32ToMonoPcm16(bytes, 1);

        Assert.Equal(0, mono[0]);
        Assert.Equal(short.MaxValue, mono[1]);
        Assert.Equal(-short.MaxValue, mono[2]);
        Assert.Equal(16384, mono[3]);
    }

    [Fact]
    public void Float32BeyondUnityClampsInsteadOfWrapping()
    {
        // Averaging two channels that both peak leaves the sum above 1.0 for one frame. Without the
        // clamp the cast wraps, and the loudest moment in the meeting comes out as the opposite sign.
        var floats = new[] { 4f, -4f };
        var bytes = new byte[floats.Length * sizeof(float)];
        Buffer.BlockCopy(floats, 0, bytes, 0, bytes.Length);

        var mono = PcmConvert.Float32ToMonoPcm16(bytes, 1);

        Assert.Equal(short.MaxValue, mono[0]);
        Assert.Equal(short.MinValue, mono[1]);
    }

    [Fact]
    public void ShortsSurviveTheByteRoundTrip()
    {
        short[] samples = [0, 1, -1, short.MaxValue, short.MinValue];

        Assert.Equal<short[]>(samples, PcmConvert.BytesToShorts(PcmConvert.ShortsToBytes(samples)));
    }

    [Fact]
    public async Task SharedCaptureConvertsTheFloat32MixFormatToTheContract()
    {
        var capture = FakeWaveIn.Float32(2, [1f, 1f, -1f, -1f, 0f, 0f]);

        var frames = WasapiPcmCapture.RunAsync(capture, TestContext.Current.CancellationToken)
            .GetAsyncEnumerator(TestContext.Current.CancellationToken);

        Assert.True(await frames.MoveNextAsync());
        Assert.Equal<short[]>(
            [short.MaxValue, -short.MaxValue, 0],
            PcmConvert.BytesToShorts(frames.Current.Span));

        await frames.DisposeAsync();
    }

    [Fact]
    public async Task SharedCaptureRefusesThirtyTwoBitIntegerRatherThanReadingItAsFloat()
    {
        // Shared mode hands over the mix format, which is float32 on every Windows Kanal targets. A
        // device reporting 32-bit integer PCM has the same sample width and none of the same
        // meaning: read as float it becomes noise around silence, which sounds like a room nobody
        // is speaking in rather than a format the host declined.
        var capture = new FakeWaveIn(
            new WaveFormat(AudioCaptureFormat.SampleRateHz, 32, 2), new byte[64]);

        var frames = WasapiPcmCapture.RunAsync(capture, TestContext.Current.CancellationToken)
            .GetAsyncEnumerator(TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<NotSupportedException>(async () => await frames.MoveNextAsync());
    }

    // Delivers from StartRecording, which the iterator calls once it has subscribed: handing the
    // buffer over any earlier drops it, and the enumerator then waits on a channel nobody writes to.
    private sealed class FakeWaveIn(WaveFormat format, byte[] payload) : IWaveIn
    {
        public static FakeWaveIn Float32(int channels, float[] samples)
        {
            var bytes = new byte[samples.Length * sizeof(float)];
            Buffer.BlockCopy(samples, 0, bytes, 0, bytes.Length);
            return new FakeWaveIn(
                WaveFormat.CreateIeeeFloatWaveFormat(AudioCaptureFormat.SampleRateHz, channels),
                bytes);
        }

        public WaveFormat WaveFormat { get; set; } = format;

        public event EventHandler<WaveInEventArgs>? DataAvailable;
        public event EventHandler<StoppedEventArgs>? RecordingStopped;

        public void StartRecording() =>
            DataAvailable?.Invoke(this, new WaveInEventArgs(payload, payload.Length));

        public void StopRecording() => RecordingStopped?.Invoke(this, new StoppedEventArgs());

        public void Dispose()
        {
        }
    }
}
