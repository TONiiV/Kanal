using System.Runtime.CompilerServices;
using System.Runtime.Versioning;
using System.Threading.Channels;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace Kanal.Audio;

internal static class WasapiPcmCapture
{
    [SupportedOSPlatform("windows")]
    internal static MMDevice OpenMicrophone(MMDeviceEnumerator enumerator, string? deviceId)
    {
        var device = deviceId is null
            ? enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications)
            : enumerator.GetDevice(deviceId);
        if (device.State == DeviceState.Active)
            return device;
        var state = device.State;
        device.Dispose();
        throw new AudioCaptureException(AudioCaptureFault.DeviceUnavailable,
            $"The selected microphone is {state}; choose an active device.");
    }

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
                // 32-bit integer PCM has float's width; decide on encoding, not width.
                var mono = (format.BitsPerSample, format.Encoding) switch
                {
                    (32, _) when format.Encoding == WaveFormatEncoding.IeeeFloat ||
                        format is WaveFormatExtensible { SubFormat: var subtype } &&
                        subtype == new Guid("00000003-0000-0010-8000-00aa00389b71") =>
                        PcmConvert.Float32ToMonoPcm16(e.Buffer.AsSpan(0, e.BytesRecorded), format.Channels),
                    (16, _) => PcmConvert.DownmixToMono(
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
