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
    [NotifyPropertyChangedFor(nameof(RuleColor))]
    private string _speakerColor = "#4C5C68";

    /// <summary>ISO code of the language actually spoken, set upper-case for the column label.</summary>
    [ObservableProperty]
    private string _sourceLang = "";

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

    /// <summary>
    /// The newest utterance in this column — set large, with a heavy rule in the speaker's colour.
    /// Exactly one bubble per column carries it; see <see cref="ColumnViewModel.GetOrAdd"/>.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RuleColor))]
    private bool _isLive;

    public bool HasSource => SourceText.Length > 0;

    /// <summary>Gray while partial (still changing), ink once final.</summary>
    public string TextColor => IsPartial ? "#7C8A93" : "#111A21";

    /// <summary>
    /// The rule above the utterance: neutral hairline for history, the speaker's colour for the
    /// live one. Colour says who, weight says now — the two signals never compete.
    /// </summary>
    public string RuleColor => IsLive ? SpeakerColor : "#D5DCE1";
}
