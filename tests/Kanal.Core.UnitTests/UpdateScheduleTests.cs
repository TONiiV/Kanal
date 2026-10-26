using Kanal.Core.Updates;

namespace Kanal.Core.UnitTests;

public class UpdateScheduleTests
{
    private sealed class ManualClock : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 10, 26, 9, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }

    [Fact]
    public void TheIntervalIsTwentyFourHours()
    {
        Assert.Equal(TimeSpan.FromHours(24), UpdateSchedule.Interval);
    }

    [Fact]
    public void ACheckIsDueAtLaunch()
    {
        var schedule = new UpdateSchedule(new ManualClock());

        Assert.True(schedule.IsDue(meetingRunning: false));
    }

    [Fact]
    public void NothingIsDueRightAfterACheck()
    {
        var schedule = new UpdateSchedule(new ManualClock());
        schedule.MarkChecked();

        Assert.False(schedule.IsDue(meetingRunning: false));
    }

    [Fact]
    public void TheNextCheckIsDueAfterTwentyFourHours()
    {
        var clock = new ManualClock();
        var schedule = new UpdateSchedule(clock);
        schedule.MarkChecked();

        clock.Advance(TimeSpan.FromHours(23) + TimeSpan.FromMinutes(59));
        Assert.False(schedule.IsDue(meetingRunning: false));

        clock.Advance(TimeSpan.FromMinutes(1));
        Assert.True(schedule.IsDue(meetingRunning: false));
    }

    [Fact]
    public void NothingIsDueWhileAMeetingRuns()
    {
        var clock = new ManualClock();
        var schedule = new UpdateSchedule(clock);

        Assert.False(schedule.IsDue(meetingRunning: true));

        schedule.MarkChecked();
        clock.Advance(TimeSpan.FromHours(30));
        Assert.False(schedule.IsDue(meetingRunning: true));
    }

    [Fact]
    public void ACheckThatFellDueDuringAMeetingIsDueAfterIt()
    {
        var clock = new ManualClock();
        var schedule = new UpdateSchedule(clock);
        schedule.MarkChecked();
        clock.Advance(TimeSpan.FromHours(25));

        Assert.False(schedule.IsDue(meetingRunning: true));
        Assert.True(schedule.IsDue(meetingRunning: false));
    }

    [Fact]
    public void ACheckRestartsTheInterval()
    {
        var clock = new ManualClock();
        var schedule = new UpdateSchedule(clock);
        schedule.MarkChecked();
        clock.Advance(TimeSpan.FromHours(25));
        schedule.MarkChecked();

        clock.Advance(TimeSpan.FromHours(23));
        Assert.False(schedule.IsDue(meetingRunning: false));

        clock.Advance(TimeSpan.FromHours(1));
        Assert.True(schedule.IsDue(meetingRunning: false));
    }
}
