using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using Kanal.Core.Insights;
using Kanal.Core.Room;

namespace Kanal.Host.ViewModels;

public sealed partial class AssistantViewModel : ViewModelBase
{
    private RoomState? _room;
    private MeetingInsights? _insights;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowNoAnalystNote))]
    [NotifyPropertyChangedFor(nameof(ShowNothingFoundYet))]
    private bool _isAnalystConnected;

    public ObservableCollection<InsightTopicViewModel> Topics { get; } = new();

    public bool ShowNoAnalystNote => !IsAnalystConnected;

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

    private void Refresh()
    {
        Topics.Clear();
        foreach (var topic in _insights?.ByTopic() ?? [])
        {
            var items = topic.Items
                .Where(item => item.State != InsightState.Dismissed)
                .Select(item => new InsightItemViewModel(item, Supporting(item), Confirm, Dismiss))
                .ToList();
            if (items.Count > 0)
                Topics.Add(new InsightTopicViewModel(topic.Topic, items));
        }

        OnPropertyChanged(nameof(ShowNothingFoundYet));
    }

    private IReadOnlyList<string> Supporting(MeetingInsight insight) =>
        insight.SourceUtteranceIds
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
