using System.Runtime.InteropServices;

namespace Kanal.Audio;

public static class PcmConvert
{
    public static short[] BytesToShorts(ReadOnlySpan<byte> pcm16)
    {
        var result = new short[pcm16.Length / 2];
        MemoryMarshal.Cast<byte, short>(pcm16[..(result.Length * 2)]).CopyTo(result);
        return result;
    }

    public static byte[] ShortsToBytes(ReadOnlySpan<short> samples)
    {
        var result = new byte[samples.Length * 2];
        MemoryMarshal.Cast<short, byte>(samples).CopyTo(result);
        return result;
    }

    /// <summary>Average interleaved multichannel PCM16 down to mono.</summary>
    public static short[] DownmixToMono(ReadOnlySpan<short> interleaved, int channels)
    {
        if (channels <= 0)
            throw new ArgumentOutOfRangeException(nameof(channels));
        if (channels == 1)
            return interleaved.ToArray();

        var frames = interleaved.Length / channels;
        var mono = new short[frames];
        for (var f = 0; f < frames; f++)
        {
            var sum = 0;
            for (var c = 0; c < channels; c++)
                sum += interleaved[f * channels + c];
            mono[f] = (short)(sum / channels);
        }

        return mono;
    }

    /// <summary>Convert interleaved float32 samples (WASAPI shared-mode mix format) to mono PCM16.</summary>
    public static short[] Float32ToMonoPcm16(ReadOnlySpan<byte> float32Interleaved, int channels)
    {
        var floats = MemoryMarshal.Cast<byte, float>(float32Interleaved);
        var frames = floats.Length / channels;
        var mono = new short[frames];
        for (var f = 0; f < frames; f++)
        {
            var sum = 0f;
            for (var c = 0; c < channels; c++)
                sum += floats[f * channels + c];
            var v = sum / channels;
            mono[f] = (short)Math.Clamp((int)Math.Round(v * short.MaxValue), short.MinValue, short.MaxValue);
        }

        return mono;
    }
}
