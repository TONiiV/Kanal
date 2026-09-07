namespace Kanal.Core.Insights;

public enum InsightKind
{
    Point,
    Proposal,
    Decision,
}

public enum InsightState
{
    Candidate,
    Confirmed,
    Dismissed,
}

public sealed record MeetingInsight(
    string Id,
    InsightKind Kind,
    string Topic,
    string Text,
    IReadOnlyList<string> SourceUtteranceIds)
{
    public InsightState State { get; init; } = InsightState.Candidate;
}

public sealed record InsightTopic(string Topic, IReadOnlyList<MeetingInsight> Items);
