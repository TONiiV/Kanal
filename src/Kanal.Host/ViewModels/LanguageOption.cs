using CommunityToolkit.Mvvm.ComponentModel;

namespace Kanal.Host.ViewModels;

/// <summary>One language in the room-language catalog: flag stack, edit dialog, room config.</summary>
public partial class LanguageOption : ViewModelBase
{
    public required string Code { get; init; }

    public required string Label { get; init; }

    public string CodeUpper => Code.ToUpperInvariant();

    [ObservableProperty]
    private bool _isSelected;
}
