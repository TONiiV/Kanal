using System.Runtime.CompilerServices;
using System.Threading.Channels;
using NAudio.Wave;

namespace Kanal.Audio;

internal static class WasapiPcmCapture
{
    internal static async IAsyncEnumerable<ReadOnlyMemory<byte>> RunAsync(
        IWaveIn capture,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var format = capture.WaveFormat;
        var resampler = format.SampleRate == AudioCaptureFormat.SampleRateHz
            ? null
            : new LinearResampler(format.SampleRate, AudioCaptureFormat.SampleRateHz);
        var frames = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(64)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
        });

        capture.DataAvailable += (_, e) =>
        {
            try
            {
                var mono = format.BitsPerSample switch
                {
                    32 => PcmConvert.Float32ToMonoPcm16(e.Buffer.AsSpan(0, e.BytesRecorded), format.Channels),
                    16 => PcmConvert.DownmixToMono(
                        PcmConvert.BytesToShorts(e.Buffer.AsSpan(0, e.BytesRecorded)), format.Channels),
                    _ => throw new NotSupportedException($"Unsupported capture format: {format}"),
                };

                short[] output;
                if (resampler is null)
                {
                    output = mono;
                }
                else
                {
                    var buffer = new short[resampler.GetMaxOutputCount(mono.Length)];
                    var count = resampler.Resample(mono, buffer);
                    output = buffer[..count];
                }

                if (output.Length > 0)
                    frames.Writer.TryWrite(PcmConvert.ShortsToBytes(output));
            }
            catch (Exception ex)
            {
                frames.Writer.TryComplete(ex);
            }
        };
        capture.RecordingStopped += (_, e) => frames.Writer.TryComplete(e.Exception);

        capture.StartRecording();
        try
        {
            await foreach (var frame in frames.Reader.ReadAllAsync(ct))
                yield return frame;
        }
        finally
        {
            capture.StopRecording();
        }
    }
}
