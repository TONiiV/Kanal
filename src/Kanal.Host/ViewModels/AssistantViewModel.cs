using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using Kanal.Core.Insights;
using Kanal.Core.Room;
using Kanal.Host.Localization;

namespace Kanal.Host.ViewModels;

public sealed partial class AssistantViewModel : ViewModelBase
{
    private RoomState? _room;
    private MeetingInsights? _insights;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowNoAnalystNote))]
    [NotifyPropertyChangedFor(nameof(ShowNothingFoundYet))]
    [NotifyPropertyChangedFor(nameof(ShowInsightList))]
    private bool _isAnalystConnected;

    public ObservableCollection<InsightTopicViewModel> Topics { get; } = new();

    public Func<string, string> SpeakerName { get; set; } = tag => tag;

    public bool ShowNoAnalystNote => !IsAnalystConnected && Topics.Count == 0;

    public bool ShowInsightList => IsAnalystConnected || Topics.Count > 0;

    public bool ShowNothingFoundYet => IsAnalystConnected && Topics.Count == 0;

    public void Follow(RoomState room)
    {
        _room = room;
        _insights = new MeetingInsights(room);
        Refresh();
    }

    public void Forget()
    {
        _room = null;
        _insights = null;
        Refresh();
    }

    public void Record(MeetingInsight insight)
    {
        if (_insights?.Record(insight) is not null)
            Refresh();
    }

    public void Pin(string utteranceId, string text)
    {
        if (_insights?.Pin(utteranceId, text) is not null)
            Refresh();
    }

    public void Refresh()
    {
        Topics.Clear();
        foreach (var topic in _insights?.ByTopic() ?? [])
        {
            var items = topic.Items
                .Where(item => item.State != InsightState.Dismissed)
                .Select(item => new InsightItemViewModel(item, Supporting(item), Confirm, Dismiss, Attribution(item)))
                .ToList();
            if (items.Count > 0)
                Topics.Add(new InsightTopicViewModel(Heading(topic.Topic), items));
        }

        OnPropertyChanged(nameof(ShowNothingFoundYet));
        OnPropertyChanged(nameof(ShowNoAnalystNote));
        OnPropertyChanged(nameof(ShowInsightList));
    }

    private static string Heading(string topic) =>
        topic == MeetingInsights.PinnedTopic ? Localizer.Instance["insight.pinned.heading"] : topic;

    private string? Attribution(MeetingInsight insight)
    {
        if (insight.Topic != MeetingInsights.PinnedTopic
            || _room?.Find(insight.SourceUtteranceIds[0]) is not { } utterance)
            return null;

        return $"{SpeakerName(utterance.SpeakerTag)} · {BubbleViewModel.Clock(utterance.TStartMs)}";
    }

    private IReadOnlyList<string> Supporting(MeetingInsight insight) =>
        insight.Topic == MeetingInsights.PinnedTopic ? [] : insight.SourceUtteranceIds
            .Select(id => _room?.Find(id)?.SrcText)
            .OfType<string>()
            .ToList();

    private void Confirm(string id)
    {
        if (_insights?.Confirm(id) is true)
            Refresh();
    }

    private void Dismiss(string id)
    {
        if (_insights?.Dismiss(id) is true)
            Refresh();
    }
}

public sealed record InsightTopicViewModel(string Topic, IReadOnlyList<InsightItemViewModel> Items);
