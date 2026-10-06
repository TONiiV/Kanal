using Kanal.Audio;

namespace Kanal.Core.UnitTests;

public class SignalWatchTests
{
    private const double Speech = 0.2;

    [Fact]
    public void NothingIsSaidAboutASourceDuringTheStartupGrace()
    {
        var clock = new StillClock();
        var watch = new SignalWatch(clock);

        clock.Advance(SignalWatch.StartupGrace - TimeSpan.FromMilliseconds(250));
        watch.Observe(receivedSamples: 0, peak: 0);

        Assert.Equal(SignalState.Pending, watch.State);
        Assert.False(watch.IsSilent);
    }

    [Fact]
    public void ASourceThatNeverDeliveredIsNamedAsNoSignal()
    {
        var clock = new StillClock();
        var watch = new SignalWatch(clock);

        clock.Advance(SignalWatch.StartupGrace);
        Assert.True(watch.Observe(receivedSamples: 0, peak: 0));

        Assert.Equal(SignalState.NoSignal, watch.State);
        Assert.True(watch.IsSilent);
    }

    [Fact]
    public void ASourceDeliveringOnlyDigitalSilenceIsNamedAsSilentSignal()
    {
        var clock = new StillClock();
        var watch = new SignalWatch(clock);

        for (var i = 1; i <= 40; i++)
        {
            clock.Advance(TimeSpan.FromMilliseconds(250));
            watch.Observe(receivedSamples: i * 4000, peak: 0.0005);
        }

        Assert.Equal(SignalState.SilentSignal, watch.State);
        Assert.True(watch.IsSilent);
    }

    [Fact]
    public void SoundClearsTheHintImmediately()
    {
        var clock = new StillClock();
        var watch = new SignalWatch(clock);
        clock.Advance(SignalWatch.StartupGrace);
        watch.Observe(0, 0);

        Assert.True(watch.Observe(receivedSamples: 4000, peak: Speech));

        Assert.Equal(SignalState.Sound, watch.State);
        Assert.False(watch.IsSilent);
    }

    [Fact]
    public void AnOrdinaryPauseInTheConversationIsNotFlagged()
    {
        var clock = new StillClock();
        var watch = new SignalWatch(clock);
        watch.Observe(4000, Speech);

        var received = 4000L;
        for (var i = 0; i < 360; i++)
        {
            clock.Advance(TimeSpan.FromMilliseconds(250));
            Assert.False(watch.Observe(received += 4000, 0), $"state changed after {i * 250} ms of pause");
        }

        Assert.Equal(SignalState.Sound, watch.State);
    }

    [Fact]
    public void ASourceThatWentQuietForLongIsFlaggedAndRecoversOnSound()
    {
        var clock = new StillClock();
        var watch = new SignalWatch(clock);
        watch.Observe(4000, Speech);

        clock.Advance(SignalWatch.QuietLimit);
        Assert.True(watch.Observe(4000, 0));
        Assert.Equal(SignalState.WentQuiet, watch.State);
        Assert.True(watch.IsSilent);

        clock.Advance(TimeSpan.FromMilliseconds(250));
        Assert.True(watch.Observe(8000, Speech));
        Assert.Equal(SignalState.Sound, watch.State);
    }

    [Fact]
    public void AStateIsReportedAsChangedOnlyOnce()
    {
        var clock = new StillClock();
        var watch = new SignalWatch(clock);
        clock.Advance(SignalWatch.StartupGrace);

        var changes = 0;
        for (var i = 0; i < 20; i++)
        {
            clock.Advance(TimeSpan.FromMilliseconds(250));
            if (watch.Observe(0, 0))
                changes++;
        }

        Assert.Equal(1, changes);
    }

    [Fact]
    public void LowRoomNoiseIsNotMistakenForSound()
    {
        var clock = new StillClock();
        var watch = new SignalWatch(clock);

        clock.Advance(SignalWatch.StartupGrace);
        watch.Observe(4000, SignalWatch.SoundPeak / 2);

        Assert.False(watch.HeardSound);
        Assert.Equal(SignalState.SilentSignal, watch.State);
    }

    [Fact]
    public void ABoundedProbeConcludesBeforeTheGraceEnds()
    {
        var clock = new StillClock();
        var silent = new SignalWatch(clock);
        var muted = new SignalWatch(clock);
        var live = new SignalWatch(clock);

        clock.Advance(TimeSpan.FromSeconds(2));
        silent.Observe(0, 0);
        muted.Observe(32000, 0);
        live.Observe(32000, Speech);

        Assert.Equal(SignalState.NoSignal, silent.Conclusion);
        Assert.Equal(SignalState.SilentSignal, muted.Conclusion);
        Assert.Equal(SignalState.Sound, live.Conclusion);
    }

    [Fact]
    public void ARemoteSourceIsJudgedOnlyAfterItsLongerGrace()
    {
        var clock = new StillClock();
        var watch = new SignalWatch(clock, SignalWatch.RemoteStartupGrace);

        clock.Advance(SignalWatch.StartupGrace);
        Assert.False(watch.Observe(0, 0));
        Assert.Equal(SignalState.Pending, watch.State);

        clock.Advance(SignalWatch.RemoteStartupGrace - SignalWatch.StartupGrace);
        Assert.True(watch.Observe(0, 0));
        Assert.Equal(SignalState.NoSignal, watch.State);
    }

    [Fact]
    public void AnUnheardSourceIsNotJudgedUntilTheCallerAllowsIt()
    {
        var clock = new StillClock();
        var watch = new SignalWatch(clock);
        clock.Advance(SignalWatch.QuietLimit);

        Assert.False(watch.Observe(32000, 0, mayConclude: false));
        Assert.Equal(SignalState.Pending, watch.State);
        Assert.False(watch.IsSilent);

        Assert.True(watch.Observe(32000, 0, mayConclude: true));
        Assert.Equal(SignalState.SilentSignal, watch.State);
    }

    [Fact]
    public void SoundAndAQuietSpellAreReportedWhetherOrNotAVerdictIsAllowed()
    {
        var clock = new StillClock();
        var watch = new SignalWatch(clock, SignalWatch.RemoteStartupGrace);

        Assert.True(watch.Observe(4000, Speech, mayConclude: false));
        Assert.Equal(SignalState.Sound, watch.State);

        clock.Advance(SignalWatch.QuietLimit);
        Assert.True(watch.Observe(8000, 0, mayConclude: false));
        Assert.Equal(SignalState.WentQuiet, watch.State);
    }

    [Fact]
    public void StatesHaveStableLogCodes()
    {
        Assert.Equal("no_signal", SignalWatch.CodeOf(SignalState.NoSignal));
        Assert.Equal("silent_signal", SignalWatch.CodeOf(SignalState.SilentSignal));
        Assert.Equal("went_quiet", SignalWatch.CodeOf(SignalState.WentQuiet));
    }

    private sealed class StillClock : TimeProvider
    {
        private long _ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _ticks;
        public void Advance(TimeSpan by) => _ticks += by.Ticks;
    }
}
