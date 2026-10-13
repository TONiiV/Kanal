using System;
using System.Collections.Generic;
using CommunityToolkit.Mvvm.Input;
using Kanal.Core.Insights;
using Kanal.Host.Localization;

namespace Kanal.Host.ViewModels;

public sealed partial class InsightItemViewModel : ViewModelBase
{
    private readonly Action<string> _confirm;
    private readonly Action<string> _dismiss;

    public InsightItemViewModel(
        MeetingInsight insight,
        IReadOnlyList<string> sources,
        Action<string> confirm,
        Action<string> dismiss,
        string? attribution = null)
    {
        Id = insight.Id;
        IsPinned = insight.Topic == MeetingInsights.PinnedTopic;
        Attribution = attribution;
        Text = insight.Text;
        Kind = insight.Kind;
        IsCandidate = insight.State == InsightState.Candidate;
        Sources = sources;
        _confirm = confirm;
        _dismiss = dismiss;
    }

    public string Id { get; }

    public bool IsPinned { get; }

    public string? Attribution { get; }

    public string Text { get; }

    public InsightKind Kind { get; }

    public bool IsCandidate { get; }

    public IReadOnlyList<string> Sources { get; }

    public string KindLabel => Localizer.Instance[
        IsPinned ? "insight.pinned" : $"insight.kind.{Kind.ToString().ToLowerInvariant()}"];

    public string StateLabel => IsPinned
        ? Attribution ?? ""
        : Localizer.Instance[IsCandidate ? "insight.candidate" : "insight.confirmed"];

    public string DismissTip => Localizer.Instance[IsPinned ? "insight.unpin.tip" : "insight.dismiss.tip"];

    [RelayCommand]
    private void Confirm() => _confirm(Id);

    [RelayCommand]
    private void Dismiss() => _dismiss(Id);
}
