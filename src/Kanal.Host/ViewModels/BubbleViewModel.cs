using CommunityToolkit.Mvvm.ComponentModel;

namespace Kanal.Host.ViewModels;

/// <summary>One utterance as rendered in one language column. Updated in place on upsert.</summary>
public partial class BubbleViewModel : ViewModelBase
{
    public required string UtteranceId { get; init; }

    [ObservableProperty]
    private string _speakerTag = "";

    [ObservableProperty]
    private string _speakerName = "";

    [ObservableProperty]
    private string _speakerColor = "#4C5C68";

    /// <summary>Primary line: the translation for this column (or source when same language).</summary>
    [ObservableProperty]
    private string _text = "";

    /// <summary>Secondary line: the original text, shown when it differs from the primary line.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSource))]
    private string _sourceText = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TextColor))]
    private bool _isPartial = true;

    [ObservableProperty]
    private bool _codeSwitch;

    public bool HasSource => SourceText.Length > 0;

    /// <summary>Gray while partial (still changing), ink once final.</summary>
    public string TextColor => IsPartial ? "#7A8791" : "#111A21";
}
