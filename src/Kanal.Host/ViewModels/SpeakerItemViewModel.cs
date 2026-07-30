using CommunityToolkit.Mvvm.ComponentModel;

namespace Kanal.Host.ViewModels;

public partial class SpeakerItemViewModel : ViewModelBase
{
    public required string Tag { get; init; }

    [ObservableProperty]
    private string _color = "#4C5C68";

    /// <summary>Editable display name; applied via the Rename command.</summary>
    [ObservableProperty]
    private string _name = "";

    [ObservableProperty]
    private string _mergedFromLabel = "";
}
