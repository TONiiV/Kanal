using System.Runtime.CompilerServices;

namespace Kanal.Audio;

/// <summary>
/// Replays a WAV file as if it were a live microphone: resampled to 16 kHz mono,
/// framed at 100 ms, optionally paced in real time. Lets the whole pipeline run
/// deterministically without a device — and lets recorded meetings be re-run.
/// </summary>
public sealed class WavFileAudioSource : IAudioCaptureService
{
    public const int TargetRateHz = 16_000;
    private const int FrameSamples = TargetRateHz / 10; // 100 ms

    private readonly string _path;
    private readonly bool _realtime;

    public WavFileAudioSource(string path, bool realtime = true)
    {
        _path = path;
        _realtime = realtime;
    }

    public IReadOnlyList<AudioDeviceInfo> GetDevices() =>
        [new AudioDeviceInfo(_path, $"WAV replay: {Path.GetFileName(_path)}")];

    public async IAsyncEnumerable<ReadOnlyMemory<byte>> CaptureAsync(
        string? deviceId, [EnumeratorCancellation] CancellationToken ct)
    {
        WavFile.WavData wav;
        await using (var stream = File.OpenRead(_path))
        {
            wav = WavFile.Read(stream);
        }

        var mono = PcmConvert.DownmixToMono(PcmConvert.BytesToShorts(wav.Pcm16), wav.Channels);
        short[] samples;
        if (wav.SampleRateHz == TargetRateHz)
        {
            samples = mono;
        }
        else
        {
            var resampler = new LinearResampler(wav.SampleRateHz, TargetRateHz);
            var buffer = new short[resampler.GetMaxOutputCount(mono.Length)];
            var count = resampler.Resample(mono, buffer);
            samples = buffer[..count];
        }

        for (var offset = 0; offset < samples.Length; offset += FrameSamples)
        {
            ct.ThrowIfCancellationRequested();
            var length = Math.Min(FrameSamples, samples.Length - offset);
            yield return PcmConvert.ShortsToBytes(samples.AsSpan(offset, length));
            if (_realtime)
                await Task.Delay(100, ct);
        }
    }
}
