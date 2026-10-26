namespace Kanal.Core.Updates;

public sealed class UpdateSchedule(TimeProvider clock)
{
    public static TimeSpan Interval { get; } = TimeSpan.FromHours(24);

    public bool IsDue(bool meetingRunning) => false;

    public void MarkChecked()
    {
    }
}
