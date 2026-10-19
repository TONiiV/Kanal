using CommunityToolkit.Mvvm.ComponentModel;
using Kanal.Core.Workspaces;

namespace Kanal.Host.ViewModels;

public sealed partial class GlossaryRowViewModel : ObservableObject
{
    public GlossaryRowViewModel()
    {
    }

    public GlossaryRowViewModel(GlossaryEntry entry)
    {
        _zh = entry.Zh;
        _de = entry.De;
        _pl = entry.Pl;
        _en = entry.En;
        _doNotTranslate = entry.DoNotTranslate;
    }

    [ObservableProperty]
    private string _zh = "";

    [ObservableProperty]
    private string _de = "";

    [ObservableProperty]
    private string _pl = "";

    [ObservableProperty]
    private string _en = "";

    [ObservableProperty]
    private bool _doNotTranslate;

    public GlossaryEntry ToEntry() => new(Zh, De, Pl, En, DoNotTranslate);
}
