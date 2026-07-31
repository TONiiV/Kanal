using System;
using System.Collections.ObjectModel;
using System.Collections.Generic;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Kanal.Host.ViewModels;

public partial class ColumnViewModel : ViewModelBase
{
    private readonly Dictionary<string, BubbleViewModel> _byId = new();
    private BubbleViewModel? _live;

    public ColumnViewModel(string language)
    {
        Language = language;
        Code = language.ToUpperInvariant();
        // column heads read as signage: the ISO code large, the language's own name beneath
        NativeName = LanguageCatalog.NativeName(language) ?? "";
    }

    public string Language { get; }

    public string Code { get; }

    public string NativeName { get; }

    public ObservableCollection<BubbleViewModel> Bubbles { get; } = new();

    /// <summary>
    /// Drop indication while a column is being dragged: a rule down the edge the dragged column
    /// would land against. Structure, not highlight — the same vocabulary as every other rule on
    /// screen, and readable from a metre away without spending a colour.
    /// </summary>
    [ObservableProperty]
    private bool _isDropBefore;

    /// <inheritdoc cref="IsDropBefore"/>
    [ObservableProperty]
    private bool _isDropAfter;

    public BubbleViewModel GetOrAdd(string utteranceId)
    {
        if (_byId.TryGetValue(utteranceId, out var bubble))
            return bubble;

        bubble = new BubbleViewModel { UtteranceId = utteranceId };
        _byId[utteranceId] = bubble;
        Bubbles.Add(bubble);

        // only the newest utterance is live; the previous one settles into history
        if (_live is not null)
            _live.IsLive = false;
        _live = bubble;
        bubble.IsLive = true;

        return bubble;
    }

    public void Clear()
    {
        _byId.Clear();
        Bubbles.Clear();
        _live = null;
    }
}
