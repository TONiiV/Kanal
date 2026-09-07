using Kanal.Core.Room;

namespace Kanal.Core.Insights;

public sealed class MeetingInsights(RoomState room)
{
    private readonly List<MeetingInsight> _items = [];

    public IReadOnlyList<MeetingInsight> Items => _items;

    // Refused rather than stored and flagged: an item with no source is the very failure the
    // candidate state exists to let a person catch.
    public MeetingInsight? Record(MeetingInsight insight)
    {
        var sources = insight.SourceUtteranceIds.Where(room.Contains).ToList();
        if (sources.Count == 0)
            return null;

        var recorded = insight with { SourceUtteranceIds = sources, State = InsightState.Candidate };
        _items.Add(recorded);
        return recorded;
    }

    public bool Confirm(string id) => MoveTo(id, InsightState.Confirmed);

    public bool Dismiss(string id) => MoveTo(id, InsightState.Dismissed);

    public IReadOnlyList<InsightTopic> ByTopic() =>
        _items
            .GroupBy(item => item.Topic, StringComparer.Ordinal)
            .Select(topic => new InsightTopic(
                topic.Key,
                topic.OrderBy(item => item.Kind).ToList()))
            .ToList();

    private bool MoveTo(string id, InsightState state)
    {
        var index = _items.FindIndex(item => item.Id == id);
        if (index < 0 || _items[index].State == InsightState.Dismissed)
            return false;

        _items[index] = _items[index] with { State = state };
        return true;
    }
}
