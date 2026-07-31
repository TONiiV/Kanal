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

    /// <summary>
    /// False for the unselected rows once the room is at <see cref="MainViewModel.MaxLanguages"/>:
    /// the checkbox is disabled rather than silently ignoring the click, and the dialog prints why.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RowOpacity))]
    private bool _isSelectable = true;

    /// <summary>Recedes an unselectable row in contrast — a disabled checkbox alone is too quiet
    /// to read from a metre away, and the row's own labels carry explicit brushes.</summary>
    public double RowOpacity => IsSelectable ? 1 : 0.4;
}
