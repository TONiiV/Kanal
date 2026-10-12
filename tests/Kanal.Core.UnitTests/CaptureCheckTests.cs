using Kanal.Audio;

namespace Kanal.Core.UnitTests;

public class CaptureCheckTests
{
    private const string Mic = OnlineMeetingCapture.MicrophoneSource;
    private const string System = OnlineMeetingCapture.SystemSource;

    [Fact]
    public void BothSourcesHeardExitsOk()
    {
        var clock = new SteppedClock();
        var check = new CaptureCheck(clock, Mic, System);

        check.Observe(Levels(Mic, 16_000, 0.4));
        check.Observe(Levels(System, 16_000, 0.2));
        clock.Advance(SignalWatch.StartupGrace);

        Assert.Equal(CaptureCheck.Ok, check.ExitCode);
        Assert.Empty(check.SilentSources);
        Assert.All(check.Summaries, s => Assert.Equal("sound", s.State));
    }

    [Fact]
    public void ASourceThatDeliveredOnlyQuietSamplesExitsSilentAndIsNamed()
    {
        var clock = new SteppedClock();
        var check = new CaptureCheck(clock, Mic, System);

        check.Observe(Levels(Mic, 32_000, 0.4));
        check.Observe(Levels(System, 32_000, SignalWatch.SoundPeak / 2));

        Assert.Equal(CaptureCheck.Silent, check.ExitCode);
        Assert.Equal([System], check.SilentSources);
        Assert.Equal("silent_signal", check.Summaries.Single(s => s.Source == System).State);
    }

    [Fact]
    public void ASourceThatNeverDeliveredIsNoSignal()
    {
        var check = new CaptureCheck(new SteppedClock(), System);

        Assert.Equal(CaptureCheck.Silent, check.ExitCode);
        Assert.Equal("no_signal", Assert.Single(check.Summaries).State);
    }

    [Fact]
    public void AFaultOutranksSilenceAndCarriesItsCodeAndSource()
    {
        var check = new CaptureCheck(new SteppedClock(), Mic, System);

        check.Observe(new CaptureDiagnostic("s", "fault", System, "abc", Error: "permission_denied"));

        Assert.Equal(CaptureCheck.Failed, check.ExitCode);
        var system = check.Summaries.Single(s => s.Source == System);
        Assert.Equal("permission_denied", system.Fault);
        Assert.Null(check.Summaries.Single(s => s.Source == Mic).Fault);
    }

    [Fact]
    public void AnExceptionWithoutADiagnosticIsRecordedAgainstItsSource()
    {
        var check = new CaptureCheck(new SteppedClock(), System);

        check.Fail(new AudioCaptureException(AudioCaptureFault.DeviceUnavailable, "gone", System));

        Assert.Equal(CaptureCheck.Failed, check.ExitCode);
        Assert.Equal("device_unavailable", Assert.Single(check.Summaries).Fault);
    }

    [Fact]
    public void AMixerFaultFailsTheCheckWithoutBlamingASource()
    {
        var check = new CaptureCheck(new SteppedClock(), Mic, System);
        check.Observe(Levels(Mic, 16_000, 0.4));
        check.Observe(Levels(System, 16_000, 0.4));

        check.Fail(new AudioCaptureException(
            AudioCaptureFault.ClockStalled, "behind", OnlineMeetingCapture.MixerSource));

        Assert.Equal(CaptureCheck.Failed, check.ExitCode);
        Assert.All(check.Summaries, s => Assert.Null(s.Fault));
    }

    [Fact]
    public void TheSummaryCarriesCountsAndTheLoudestPeakPerSource()
    {
        var check = new CaptureCheck(new SteppedClock(), Mic);

        check.Observe(Levels(Mic, 4_000, 0.5, dropped: 3, padded: 7));
        check.Observe(Levels(Mic, 8_000, 0.1, dropped: 5, padded: 9));

        var summary = Assert.Single(check.Summaries);
        Assert.Equal(8_000, summary.ReceivedSamples);
        Assert.Equal(5, summary.DroppedSamples);
        Assert.Equal(9, summary.PaddedSamples);
        Assert.Equal(0.5, summary.Peak);
    }

    [Fact]
    public void LevelsArePrintedOncePerIntervalPerSourceEvenFromManyThreads()
    {
        var clock = new SteppedClock();
        var check = new CaptureCheck(clock, Mic, System);
        var printed = 0;

        Parallel.For(0, 1_000, i =>
        {
            if (check.Observe(Levels(i % 2 == 0 ? Mic : System, i, 0.2)))
                Interlocked.Increment(ref printed);
        });
        Assert.Equal(2, printed);

        clock.Advance(CaptureCheck.PrintInterval);
        Assert.True(check.Observe(Levels(Mic, 2_000, 0.2)));
        Assert.False(check.Observe(Levels(Mic, 2_100, 0.2)));
    }

    [Fact]
    public void EventsOtherThanLevelsAreAlwaysPrinted()
    {
        var check = new CaptureCheck(new SteppedClock(), Mic);

        Assert.True(check.Observe(new CaptureDiagnostic("s", "starting", Mic, "default")));
        Assert.True(check.Observe(new CaptureDiagnostic("s", "first_frame", Mic, "default")));
    }

    private static CaptureDiagnostic Levels(string source, long received, double peak, long dropped = 0, long padded = 0) =>
        new("s", "levels", source, "default", received, dropped, padded, peak);

    private sealed class SteppedClock : TimeProvider
    {
        private long _ticks;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp() => Interlocked.Read(ref _ticks);

        public void Advance(TimeSpan by) => Interlocked.Add(ref _ticks, by.Ticks);
    }
}
