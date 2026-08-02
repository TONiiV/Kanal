using System.Runtime.InteropServices;

namespace Kanal.Audio;

/// <summary>What the room's microphone is actually delivering, said in one word.</summary>
public enum InputVerdict
{
    /// <summary>Nothing is arriving. Wrong device, muted in Windows, or unplugged.</summary>
    Silent,

    /// <summary>Arriving, but so quietly that a metre away it will be lost in the room.</summary>
    TooQuiet,

    /// <summary>Hitting the ceiling. Consonants are being clipped off, which no ASR recovers.</summary>
    Clipping,

    /// <summary>Loud enough to transcribe, but the room is nearly as loud as the speaker.</summary>
    Noisy,

    Good,
}

/// <summary>
/// Measures a capture stream the way an operator needs it judged before a meeting: is anything
/// arriving, is it loud enough, is it clipping, and how far above the room's own noise it sits.
/// </summary>
/// <remarks>
/// Everything is in dBFS, where 0 is the loudest a 16-bit sample can be. The noise floor is the
/// quietest recent frame rather than an average: between sentences a meeting room is at its
/// floor, so the quiet frames are the honest measure of what the microphone hears when nobody is
/// speaking. Levels are computed here rather than in the view so they can be tested against
/// generated audio instead of against a room.
/// </remarks>
public sealed class LevelMeter
{
    /// <summary>Quieter than this is indistinguishable from a dead input.</summary>
    public const double SilenceDb = -55.0;

    /// <summary>Below this, a speaker a metre from the microphone will not survive the room.</summary>
    public const double TooQuietDb = -34.0;

    /// <summary>Under this much room between speech and silence, the room competes with the speaker.</summary>
    public const double MinimumMarginDb = 14.0;

    /// <summary>Floor for the log, and what an all-zero frame reports.</summary>
    public const double FloorDb = -90.0;

    private readonly List<double> _frameLevels = new();
    private readonly int _history;

    /// <param name="history">
    /// How many recent frames the noise floor is drawn from. Both capture backends deliver
    /// 100 ms frames, so the default keeps roughly the last 40 seconds — comfortably enough
    /// gaps between sentences for the 10th percentile to be a real between-speech floor.
    /// </param>
    public LevelMeter(int history = 400) => _history = history;

    /// <summary>Level of the most recent frame, in dBFS.</summary>
    public double CurrentDb { get; private set; } = FloorDb;

    /// <summary>The loudest frame seen since the last <see cref="Reset"/> — what speech reaches.</summary>
    public double PeakDb { get; private set; } = FloorDb;

    /// <summary>True once any sample has reached full scale.</summary>
    public bool HasClipped { get; private set; }

    public int Frames { get; private set; }

    /// <summary>
    /// The quietest recent frame: what the microphone hears when nobody is speaking. Reported as
    /// the 10th percentile rather than the single minimum, so one anomalous frame cannot claim
    /// the room is silent.
    /// </summary>
    public double NoiseFloorDb
    {
        get
        {
            if (_frameLevels.Count == 0)
                return FloorDb;

            var sorted = _frameLevels.Order().ToList();
            return sorted[Math.Min(sorted.Count - 1, sorted.Count / 10)];
        }
    }

    /// <summary>How far speech sits above the room. Under <see cref="MinimumMarginDb"/> is a problem.</summary>
    public double MarginDb => PeakDb <= FloorDb ? 0 : PeakDb - NoiseFloorDb;

    /// <summary>
    /// False when the gaps between speech are digital silence — a device delivering zeros, or a
    /// driver gating hard. The margin is then measured against a clamp rather than against a
    /// room, and quoting it ("81 dB above the room") is precision the number does not have.
    /// </summary>
    public bool HasMeasurableNoise => NoiseFloorDb > FloorDb + 1;

    public InputVerdict Verdict
    {
        get
        {
            if (Frames == 0 || PeakDb < SilenceDb)
                return InputVerdict.Silent;
            if (HasClipped)
                return InputVerdict.Clipping;
            if (PeakDb < TooQuietDb)
                return InputVerdict.TooQuiet;
            // only meaningful once there has been both speech and a gap to compare it against
            if (_frameLevels.Count >= 10 && MarginDb < MinimumMarginDb)
                return InputVerdict.Noisy;
            return InputVerdict.Good;
        }
    }

    public void Add(ReadOnlySpan<byte> pcm16)
    {
        if (pcm16.Length < 2)
            return;

        var samples = MemoryMarshal.Cast<byte, short>(pcm16);
        double sumSquares = 0;
        var peak = 0;

        foreach (var sample in samples)
        {
            // -32768 has no positive counterpart; negating it overflows back to itself
            var magnitude = sample == short.MinValue ? short.MaxValue : Math.Abs((int)sample);
            if (magnitude >= short.MaxValue)
                HasClipped = true;
            peak = Math.Max(peak, magnitude);
            sumSquares += (double)sample * sample;
        }

        var rms = Math.Sqrt(sumSquares / samples.Length) / short.MaxValue;
        CurrentDb = ToDb(rms);
        PeakDb = Math.Max(PeakDb, ToDb(peak / (double)short.MaxValue));
        Frames++;

        _frameLevels.Add(CurrentDb);
        if (_frameLevels.Count > _history)
            _frameLevels.RemoveAt(0);
    }

    public void Reset()
    {
        _frameLevels.Clear();
        CurrentDb = FloorDb;
        PeakDb = FloorDb;
        HasClipped = false;
        Frames = 0;
    }

    /// <summary>0–100 for a progress bar, mapping <see cref="FloorDb"/>..0 dBFS onto the range.</summary>
    public static double ToScale(double db) => Math.Clamp((db - FloorDb) / -FloorDb * 100.0, 0, 100);

    private static double ToDb(double amplitude) =>
        amplitude <= 0 ? FloorDb : Math.Max(FloorDb, 20 * Math.Log10(amplitude));
}
