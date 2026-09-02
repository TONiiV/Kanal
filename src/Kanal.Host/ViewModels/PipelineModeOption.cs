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

    public string Help => Mode.Help;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsAvailable))]
    [NotifyPropertyChangedFor(nameof(Detail))]
    [NotifyPropertyChangedFor(nameof(Status))]
    private string? _unavailable;

    public bool IsAvailable => Unavailable is null;

    /// <summary>
    /// Whether this row can be picked, in words. Availability used to be carried only by the
    /// row's contrast — the same signal the grey second line already uses — so at a glance the
    /// list read as five equal choices and the operator found out at Start.
    /// </summary>
    public string Status => Unavailable ?? Localization.Localizer.Instance["mode.status.ready"];

    /// <summary>Re-reads every string on this row after the application's language changes.</summary>
    public void RefreshText()
    {
        OnPropertyChanged(nameof(Name));
        OnPropertyChanged(nameof(Help));
        OnPropertyChanged(nameof(Detail));
        OnPropertyChanged(nameof(Status));
    }

    /// <summary>The second line: the privacy consequence, then the blocker if there is one.</summary>
    public string Detail => IsAvailable ? Mode.Leaves : $"{Mode.Leaves} · {Unavailable}";
}
