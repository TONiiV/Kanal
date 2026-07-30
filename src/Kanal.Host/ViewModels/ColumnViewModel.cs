using System.Collections.ObjectModel;
using System.Collections.Generic;

namespace Kanal.Host.ViewModels;

public partial class ColumnViewModel : ViewModelBase
{
    private readonly Dictionary<string, BubbleViewModel> _byId = new();

    public ColumnViewModel(string language) => Language = language;

    public string Language { get; }

    public ObservableCollection<BubbleViewModel> Bubbles { get; } = new();

    public BubbleViewModel GetOrAdd(string utteranceId)
    {
        if (_byId.TryGetValue(utteranceId, out var bubble))
            return bubble;

        bubble = new BubbleViewModel { UtteranceId = utteranceId };
        _byId[utteranceId] = bubble;
        Bubbles.Add(bubble);
        return bubble;
    }

    public void Clear()
    {
        _byId.Clear();
        Bubbles.Clear();
    }
}
