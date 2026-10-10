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
    private string _speakerColor = ThemeColours.SpeakerFallback;

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
    private bool _isPartial = true;

    /// <summary>True in the column whose language was spoken — the text is a transcript, not a translation.</summary>
    [ObservableProperty]
    private bool _isTranscript;

    /// <summary>True while a non-source column waits for its translation; the body shows a muted ellipsis.</summary>
    [ObservableProperty]
    private bool _awaitingTranslation;

    [ObservableProperty]
    private bool _codeSwitch;

    /// <summary>
    /// The newest utterance in this column — set large, with a heavy rule in the speaker's colour.
    /// Exactly one bubble per column carries it; see <see cref="ColumnViewModel.GetOrAdd"/>.
    /// </summary>
    [ObservableProperty]
    private bool _isLive;

    [ObservableProperty]
    private bool _isJumpTarget;

    public bool HasSource => SourceText.Length > 0;
}
