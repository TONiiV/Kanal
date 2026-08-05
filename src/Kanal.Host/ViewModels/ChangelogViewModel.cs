using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Kanal.Host.Localization;
using Kanal.Host.Services;

namespace Kanal.Host.ViewModels;

public sealed class ChangelogEntryViewModel(ChangelogRelease release)
{
    public string Version { get; } = release.Version;

    // Invariant: a build identifier, not a date the operator reads in their own calendar.
    public string Date { get; } =
        release.Date?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "";

    public IReadOnlyList<string> Changes { get; } = release.Changes;
}

public sealed class ChangelogViewModel : ViewModelBase
{
    public ChangelogViewModel()
        : this(Changelog.Releases)
    {
    }

    public ChangelogViewModel(IReadOnlyList<ChangelogRelease> releases)
    {
        Entries = releases.Select(r => new ChangelogEntryViewModel(r)).ToList();
    }

    public IReadOnlyList<ChangelogEntryViewModel> Entries { get; }

    public bool IsEmpty => Entries.Count == 0;

    public string CurrentVersion => Localizer.Instance.Format("settings.about.version", AppVersion.Current);
}
