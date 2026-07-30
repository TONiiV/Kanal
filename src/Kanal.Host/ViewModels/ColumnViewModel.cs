using System;
using System.Collections.ObjectModel;
using System.Collections.Generic;

namespace Kanal.Host.ViewModels;

public partial class ColumnViewModel : ViewModelBase
{
    /// <summary>Column heads read as signage: the ISO code large, the language's own name beneath.</summary>
    private static readonly Dictionary<string, string> NativeNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["zh"] = "中文",
        ["de"] = "Deutsch",
        ["pl"] = "Polski",
        ["en"] = "English",
        ["fr"] = "Français",
        ["es"] = "Español",
        ["it"] = "Italiano",
        ["cs"] = "Čeština",
        ["uk"] = "Українська",
        ["ru"] = "Русский",
        ["ja"] = "日本語",
        ["ko"] = "한국어",
    };

    private readonly Dictionary<string, BubbleViewModel> _byId = new();
    private BubbleViewModel? _live;

    public ColumnViewModel(string language)
    {
        Language = language;
        Code = language.ToUpperInvariant();
        NativeName = NativeNames.TryGetValue(language, out var name) ? name : "";
    }

    public string Language { get; }

    public string Code { get; }

    public string NativeName { get; }

    public ObservableCollection<BubbleViewModel> Bubbles { get; } = new();

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
