using System.Collections.Generic;
using System.Linq;
using Kanal.Host.Localization;
using Kanal.Host.Services;

namespace Kanal.Host.ViewModels;

/// <summary>One version's entry as the dialog renders it: a dated heading over its lines.</summary>
public sealed class ChangelogEntryViewModel(ChangelogRelease release)
{
    public string Version { get; } = release.Version;

    /// <summary>ISO, not a localised date: it is a build identifier, read beside a version number.</summary>
    public string Date { get; } = release.Date?.ToString("yyyy-MM-dd") ?? "";

    public IReadOnlyList<string> Changes { get; } = release.Changes;
}

/// <summary>
/// What changed, newest first, read from the changelog embedded in this build. Nothing here is
/// fetched: the laptop running a meeting is often the one that cannot reach the internet.
/// </summary>
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
