using CommunityToolkit.Mvvm.ComponentModel;
using Kanal.Host.Services;

namespace Kanal.Host.ViewModels;

/// <summary>
/// One row of the MODE list. Always shows what the mode would send off this machine, and — when
/// it cannot run — why, in the row itself. Unavailable modes are shown rather than hidden:
/// hiding them hides the roadmap, and offering them only to fail at Start is worse than both.
/// </summary>
public partial class PipelineModeOption : ViewModelBase
{
    public PipelineModeOption(PipelineMode mode, string? unavailable)
    {
        Mode = mode;
        _unavailable = unavailable;
    }

    public PipelineMode Mode { get; }

    public string Name => Mode.Name;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsAvailable))]
    [NotifyPropertyChangedFor(nameof(Detail))]
    private string? _unavailable;

    public bool IsAvailable => Unavailable is null;

    /// <summary>The second line: the privacy consequence, then the blocker if there is one.</summary>
    public string Detail => IsAvailable ? Mode.Leaves : $"{Mode.Leaves} · {Unavailable}";
}
