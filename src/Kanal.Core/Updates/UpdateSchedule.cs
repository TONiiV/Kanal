namespace Kanal.Core.Updates;

public sealed class UpdateSchedule(TimeProvider clock)
{
    private DateTimeOffset? _lastCheck;

    public static TimeSpan Interval { get; } = TimeSpan.FromHours(24);

    public bool IsDue(bool meetingRunning) =>
        !meetingRunning && (_lastCheck is null || clock.GetUtcNow() - _lastCheck >= Interval);

    public void MarkChecked() => _lastCheck = clock.GetUtcNow();
}
