using Kanal.Core.Models;

namespace Kanal.Core.Insights;

public sealed record MeetingAgentCapabilities(IReadOnlySet<InsightKind> Kinds);

public sealed record MeetingAgentWindow(
    IReadOnlyList<Utterance> Finals,
    IReadOnlyList<string> KnownTopics,
    string ReplyLanguage);

public interface IMeetingAgent
{
    string Id { get; }

    MeetingAgentCapabilities Caps { get; }

    // The ids and states of what comes back are ignored: the runner assigns both (ADR 0058).
    Task<IReadOnlyList<MeetingInsight>> ProposeAsync(MeetingAgentWindow window, CancellationToken ct);
}
