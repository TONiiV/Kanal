using Kanal.Audio;

namespace Kanal.Tests;

/// <summary>
/// The four things an operator needs settled before a meeting starts, judged against generated
/// audio rather than against a room: is anything arriving, is it loud enough, is it clipping,
/// and does the speaker actually stand above the room.
/// </summary>
public class LevelMeterTests
{
    /// <summary>A sine at a given fraction of full scale — the closest thing to a steady voice.</summary>
    private static byte[] Tone(double amplitude, int samples = 1600)
    {
        var pcm = new byte[samples * 2];
        for (var i = 0; i < samples; i++)
        {
            var value = (short)Math.Round(short.MaxValue * amplitude * Math.Sin(i * 0.12));
            pcm[i * 2] = (byte)(value & 0xFF);
            pcm[i * 2 + 1] = (byte)((value >> 8) & 0xFF);
        }

        return pcm;
    }

    private static byte[] Silence(int samples = 1600) => new byte[samples * 2];

    private static void Feed(LevelMeter meter, byte[] frame, int times)
    {
        for (var i = 0; i < times; i++)
            meter.Add(frame);
    }

    [Fact]
    public void NothingArrivingReadsAsSilent()
    {
        var meter = new LevelMeter();

        Assert.Equal(InputVerdict.Silent, meter.Verdict);

        Feed(meter, Silence(), 20);

        Assert.Equal(InputVerdict.Silent, meter.Verdict);
        Assert.Equal(LevelMeter.FloorDb, meter.PeakDb);
    }

    /// <summary>
    /// The failure this exists to catch: the meeting starts, the operator sees the columns fill
    /// with nothing, and the cause was a microphone at 4% that nobody looked at.
    /// </summary>
    [Fact]
    public void AVeryQuietMicrophoneIsCalledOut()
    {
        var meter = new LevelMeter();

        Feed(meter, Silence(), 20);
        Feed(meter, Tone(0.01), 20);

        Assert.Equal(InputVerdict.TooQuiet, meter.Verdict);
        Assert.InRange(meter.PeakDb, LevelMeter.SilenceDb, LevelMeter.TooQuietDb);
    }

    [Fact]
    public void FullScaleAudioIsCalledOutAsClipping()
    {
        var meter = new LevelMeter();

        Feed(meter, Silence(), 20);
        Feed(meter, Tone(1.0), 10);

        Assert.True(meter.HasClipped);
        Assert.Equal(InputVerdict.Clipping, meter.Verdict);
    }

    /// <summary>Clipping is a fault about the whole take, not about the newest frame.</summary>
    [Fact]
    public void ClippingIsRememberedUntilReset()
    {
        var meter = new LevelMeter();
        Feed(meter, Silence(), 20);
        Feed(meter, Tone(1.0), 5);
        Feed(meter, Tone(0.3), 40);

        Assert.Equal(InputVerdict.Clipping, meter.Verdict);

        meter.Reset();
        Feed(meter, Silence(), 20);
        Feed(meter, Tone(0.3), 40);

        Assert.False(meter.HasClipped);
        Assert.Equal(InputVerdict.Good, meter.Verdict);
    }

    [Fact]
    public void AHealthyMicrophoneInAQuietRoomIsGood()
    {
        var meter = new LevelMeter();

        Feed(meter, Silence(), 40);   // the gaps between sentences
        Feed(meter, Tone(0.35), 40);  // the sentences

        Assert.Equal(InputVerdict.Good, meter.Verdict);
        Assert.True(meter.MarginDb > LevelMeter.MinimumMarginDb, $"margin was {meter.MarginDb:0.0} dB");
    }

    /// <summary>
    /// A loud microphone in a loud room passes every single-number check and still transcribes
    /// badly. What matters is the distance between the speaker and the room, not either alone.
    /// </summary>
    [Fact]
    public void ALoudRoomIsCalledOutEvenWhenTheLevelLooksFine()
    {
        var meter = new LevelMeter();

        Feed(meter, Tone(0.25), 40);  // constant room noise — fans, a projector, the street
        Feed(meter, Tone(0.4), 40);   // speech barely above it

        Assert.Equal(InputVerdict.Noisy, meter.Verdict);
        Assert.True(meter.MarginDb < LevelMeter.MinimumMarginDb, $"margin was {meter.MarginDb:0.0} dB");
    }

    /// <summary>One anomalous quiet frame must not let a noisy room claim to be silent.</summary>
    [Fact]
    public void TheNoiseFloorIgnoresASingleOutlier()
    {
        var meter = new LevelMeter();

        Feed(meter, Tone(0.25), 60);
        meter.Add(Silence()); // one dropout
        Feed(meter, Tone(0.4), 20);

        Assert.True(
            meter.NoiseFloorDb > -50,
            $"one silent frame dragged the floor to {meter.NoiseFloorDb:0.0} dB");
    }

    /// <summary>
    /// Digital silence between sentences is not a very quiet room — it is a device delivering
    /// zeros, or a driver gating hard. The margin is then measured against the clamp rather than
    /// against anything real, so the panel must not quote it as if it were a measurement.
    /// </summary>
    [Fact]
    public void DigitalSilenceIsNotReportedAsAMeasuredRoom()
    {
        var meter = new LevelMeter();
        Feed(meter, Silence(), 40);
        Feed(meter, Tone(0.35), 40);

        Assert.False(meter.HasMeasurableNoise);
        Assert.Equal(InputVerdict.Good, meter.Verdict);
    }

    [Fact]
    public void ARealRoomFloorIsMeasurable()
    {
        var meter = new LevelMeter();
        Feed(meter, Tone(0.004), 40); // quiet room tone, well above digital zero
        Feed(meter, Tone(0.35), 40);

        Assert.True(meter.HasMeasurableNoise);
        Assert.Equal(InputVerdict.Good, meter.Verdict);
    }

    [Fact]
    public void TheFloorIsBoundedSoTheBarNeverGoesNegative()
    {
        Assert.Equal(0, LevelMeter.ToScale(LevelMeter.FloorDb));
        Assert.Equal(100, LevelMeter.ToScale(0));
        Assert.InRange(LevelMeter.ToScale(-45), 0, 100);
        Assert.Equal(0, LevelMeter.ToScale(-200));
    }

    /// <summary>short.MinValue has no positive counterpart; negating it overflows back to itself.</summary>
    [Fact]
    public void TheMostNegativeSampleCountsAsClipping()
    {
        var meter = new LevelMeter();
        meter.Add([0x00, 0x80, 0x00, 0x80]); // two samples of -32768

        Assert.True(meter.HasClipped);
        Assert.True(meter.PeakDb > -1);
    }

    [Fact]
    public void OnlyRecentAudioCountsTowardsTheFloor()
    {
        var meter = new LevelMeter(history: 20);

        Feed(meter, Silence(), 30);   // an old quiet stretch, now out of the window
        Feed(meter, Tone(0.25), 30);

        Assert.True(meter.NoiseFloorDb > -50, $"stale frames still set the floor ({meter.NoiseFloorDb:0.0} dB)");
    }
}
