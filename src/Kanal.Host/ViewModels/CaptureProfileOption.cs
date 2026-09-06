using Kanal.Host.Localization;
using Kanal.Host.Services;

namespace Kanal.Host.ViewModels;

/// <summary>One capture choice and, while it is staged, the reason it cannot start yet.</summary>
public sealed class CaptureProfileOption(CaptureProfile profile) : ViewModelBase
{
    public CaptureProfile Profile { get; } = profile;

    public CaptureProfileId Id => Profile.Id;

    public string Name => Localizer.Instance[Profile.NameKey];

    public string Guidance => Localizer.Instance[Profile.GuidanceKey];

    public string? Unavailable => Profile.UnavailableKey is null
        ? null
        : Localizer.Instance[Profile.UnavailableKey];

    public bool IsAvailable => Unavailable is null;

    public void RefreshText()
    {
        OnPropertyChanged(nameof(Name));
        OnPropertyChanged(nameof(Guidance));
        OnPropertyChanged(nameof(Unavailable));
    }
}
