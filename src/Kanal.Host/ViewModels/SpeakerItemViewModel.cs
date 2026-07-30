using System;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Kanal.Host.ViewModels;

public partial class SpeakerItemViewModel : ViewModelBase
{
    private readonly Action<SpeakerItemViewModel> _applyRename;

    public SpeakerItemViewModel(Action<SpeakerItemViewModel> applyRename) =>
        _applyRename = applyRename;

    public required string Tag { get; init; }

    [ObservableProperty]
    private string _color = "#4C5C68";

    /// <summary>Editable display name; committed via <see cref="RenameCommand"/> (✓ or Enter).</summary>
    [ObservableProperty]
    private string _name = "";

    [ObservableProperty]
    private string _mergedFromLabel = "";

    [RelayCommand]
    private void Rename() => _applyRename(this);
}
