using System;
using System.Collections.Generic;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kanal.Core.Room;

namespace Kanal.Host.ViewModels;

public sealed record MergeTarget(string Tag, string Label)
{
    public override string ToString() => Label;
}

public sealed partial class ColourChoice(string value, Action<string> pick) : ObservableObject
{
    public string Value { get; } = value;

    [ObservableProperty]
    private bool _isSelected;

    [RelayCommand]
    private void Pick() => pick(Value);
}

public partial class SpeakerItemViewModel : ViewModelBase
{
    private readonly Action<SpeakerItemViewModel> _applyRename;
    private readonly Action<SpeakerItemViewModel, string> _applyMerge;
    private readonly Action<SpeakerItemViewModel, string> _applyColour;

    public SpeakerItemViewModel(
        Action<SpeakerItemViewModel> applyRename,
        Action<SpeakerItemViewModel, string> applyMerge,
        Action<SpeakerItemViewModel, string> applyColour)
    {
        _applyRename = applyRename;
        _applyMerge = applyMerge;
        _applyColour = applyColour;
        Colours = RoomState.Palette.Select(c => new ColourChoice(c, v => _applyColour(this, v))).ToList();
    }

    public required string Tag { get; init; }

    public IReadOnlyList<ColourChoice> Colours { get; }

    [ObservableProperty]
    private string _color = "";

    /// <summary>Editable display name; committed via <see cref="RenameCommand"/> (✓ or Enter).</summary>
    [ObservableProperty]
    private string _name = "";

    [ObservableProperty]
    private string _mergedFromLabel = "";

    [ObservableProperty]
    private int _utteranceCount;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanMerge))]
    private IReadOnlyList<MergeTarget> _mergeTargets = [];

    [ObservableProperty]
    private MergeTarget? _selectedMergeTarget;

    public bool CanMerge => MergeTargets.Count > 0;

    partial void OnColorChanged(string value)
    {
        foreach (var choice in Colours)
            choice.IsSelected = string.Equals(choice.Value, value, StringComparison.OrdinalIgnoreCase);
    }

    partial void OnSelectedMergeTargetChanged(MergeTarget? value)
    {
        if (value is not null)
            _applyMerge(this, value.Tag);
    }

    [RelayCommand]
    private void Rename() => _applyRename(this);
}
