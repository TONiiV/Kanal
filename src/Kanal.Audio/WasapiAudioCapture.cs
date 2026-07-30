using System.Runtime.CompilerServices;
using System.Runtime.Versioning;
using System.Threading.Channels;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace Kanal.Audio;

/// <summary>
/// Windows capture via WASAPI shared mode. Accepts whatever mix format the device
/// delivers (typically float32 stereo at 44.1/48 kHz) and converts to 16 kHz mono PCM16.
/// The macOS counterpart is the open D0-A item and lives behind the same interface.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WasapiAudioCapture : IAudioCaptureService
{
    public const int TargetRateHz = 16_000;

    public IReadOnlyList<AudioDeviceInfo> GetDevices()
    {
        using var enumerator = new MMDeviceEnumerator();
        return enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active)
            .Select(d => new AudioDeviceInfo(d.ID, d.FriendlyName))
            .ToList();
    }

    public async IAsyncEnumerable<ReadOnlyMemory<byte>> CaptureAsync(
        string? deviceId, [EnumeratorCancellation] CancellationToken ct)
    {
        using var enumerator = new MMDeviceEnumerator();
        using var device = deviceId is null
            ? enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications)
            : enumerator.GetDevice(deviceId);
        using var capture = new WasapiCapture(device);

        var format = capture.WaveFormat;
        var resampler = format.SampleRate == TargetRateHz ? null : new LinearResampler(format.SampleRate, TargetRateHz);
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
