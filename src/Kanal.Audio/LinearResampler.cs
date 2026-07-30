namespace Kanal.Audio;

/// <summary>
/// Stateful linear-interpolation resampler for mono PCM16 streams.
/// Cross-platform by construction (MediaFoundationResampler is Windows-only).
/// Carries the fractional read position and last sample across chunks, so
/// feeding a stream in arbitrary chunk sizes yields the same output as one pass.
/// </summary>
public sealed class LinearResampler
{
    private readonly double _step; // source samples advanced per output sample
    private double _t;             // next source read position, relative to current chunk start
    private short _last;           // source sample at virtual index -1 of the current chunk

    public LinearResampler(int sourceRateHz, int targetRateHz)
    {
        if (sourceRateHz <= 0 || targetRateHz <= 0)
            throw new ArgumentOutOfRangeException(nameof(sourceRateHz), "Rates must be positive.");
        SourceRateHz = sourceRateHz;
        TargetRateHz = targetRateHz;
        _step = (double)sourceRateHz / targetRateHz;
    }

    public int SourceRateHz { get; }
    public int TargetRateHz { get; }

    /// <summary>Upper bound of output samples produced for an input of the given length.</summary>
    public int GetMaxOutputCount(int inputLength) =>
        (int)Math.Ceiling((inputLength + 1) / _step) + 1;

    /// <summary>
    /// Resample a chunk. Returns the number of samples written to <paramref name="output"/>.
    /// </summary>
    public int Resample(ReadOnlySpan<short> input, Span<short> output)
    {
        if (input.IsEmpty)
            return 0;

        var written = 0;
        while (_t < input.Length)
        {
            var i0 = (int)Math.Floor(_t);
            var i1 = i0 + 1;
            if (i1 >= input.Length && _t > i0)
                break; // interpolation partner arrives with the next chunk

            var s0 = i0 < 0 ? _last : input[i0];
            if (_t <= i0)
            {
                output[written++] = s0;
            }
            else
            {
                var s1 = input[i1];
                var frac = _t - i0;
                output[written++] = (short)Math.Clamp(Math.Round(s0 + (s1 - s0) * frac), short.MinValue, short.MaxValue);
            }

            _t += _step;
        }

        _last = input[^1];
        _t -= input.Length;
        return written;
    }
}
