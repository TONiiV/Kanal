using CommunityToolkit.Mvvm.ComponentModel;

namespace Kanal.Host.ViewModels;

/// <summary>One selectable language chip in the toolbar.</summary>
public partial class LanguageOption : ViewModelBase
{
    public required string Code { get; init; }

    public required string Label { get; init; }

    [ObservableProperty]
    private bool _isSelected;
}
