using System.Collections.Concurrent;
using Kanal.Core.Insights;

namespace Kanal.Core.UnitTests;

public sealed class FakeMeetingAgent(
    Func<MeetingAgentWindow, CancellationToken, Task<IReadOnlyList<MeetingInsight>>> propose,
    params InsightKind[] kinds) : IMeetingAgent
{
    public FakeMeetingAgent(Func<MeetingAgentWindow, IReadOnlyList<MeetingInsight>> propose, params InsightKind[] kinds)
        : this((window, _) => Task.FromResult(propose(window)), kinds)
    {
    }

    public ConcurrentQueue<MeetingAgentWindow> Windows { get; } = new();

    public string Id => "fake-agent";

    public MeetingAgentCapabilities Caps { get; } = new(
        kinds.Length == 0 ? new HashSet<InsightKind>(Enum.GetValues<InsightKind>()) : new HashSet<InsightKind>(kinds));

    public Task<IReadOnlyList<MeetingInsight>> ProposeAsync(MeetingAgentWindow window, CancellationToken ct)
    {
        Windows.Enqueue(window);
        return propose(window, ct);
    }
}
