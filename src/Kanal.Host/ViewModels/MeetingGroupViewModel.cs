using System;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kanal.Host.Localization;

namespace Kanal.Host.ViewModels;

public enum MeetingAge
{
    Today,
    Yesterday,
    Recent,
}

public sealed partial class MeetingGroupViewModel(MeetingAge age, Action<MeetingItemViewModel> select)
    : ViewModelBase
{
    public MeetingAge Age { get; } = age;

    public string Title => Localizer.Instance[$"workspace.group.{Age.ToString().ToLowerInvariant()}"];

    public ObservableCollection<MeetingItemViewModel> Items { get; } = new();

    [ObservableProperty]
    private bool _isExpanded = true;

    [ObservableProperty]
    private MeetingItemViewModel? _selected;

    partial void OnSelectedChanged(MeetingItemViewModel? value)
    {
        if (value is not null)
            select(value);
    }

    [RelayCommand]
    private void Toggle() => IsExpanded = !IsExpanded;

    public void OnLanguageChanged() => OnPropertyChanged(nameof(Title));
}
