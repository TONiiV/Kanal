using System.Globalization;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using Kanal.Core.Diagnostics;
using Kanal.Host.Services;
using Kanal.Host.ViewModels;
using Kanal.Host.Views;

namespace Kanal.UI.UnitTests;

/// <summary>
/// The two new dialogs are built from XAML, and a mistyped binding path or a value that will not
/// convert to the control's type fails at runtime with an empty control rather than at build time.
/// These load the real windows and read back what the bindings produced — no assertions about
/// pixels, layout or style, which stay out of scope.
/// </summary>
public class SettingsWindowBindingTests
{
    [AvaloniaFact]
    public void TheLogPanelBindsToTheChosenLevelAndSize()
    {
        // Constructor-injected, not assigned after: setting DataContext afterwards still ran the
        // production view model first — the developer's real settings file, their real
        // microphones, and a native hot-plug listener that then outlived the window.
        var window = new SettingsWindow(new SettingsViewModel(
            new AppSettings { LogLevel = LogLevel.Error, LogMaxFileSizeMb = 33 },
            () => null,
            isMacOs: false,
            deviceWatcherFactory: null,
            openFolder: _ => { }));
        window.Show();

        var levels = window.GetLogicalDescendants().OfType<ComboBox>()
            .Single(c => c.SelectedItem is LogLevelOption);
        Assert.Equal(LogLevel.Error, ((LogLevelOption)levels.SelectedItem!).Level);

        // decimal? on the control, int in the settings file: the conversion happens in the
        // binding, which is exactly the sort of thing that only shows up at runtime
        var size = Assert.Single(window.GetLogicalDescendants().OfType<NumericUpDown>());
        Assert.Equal(33m, size.Value);
        Assert.Equal(SettingsStore.MaxLogMaxFileSizeMb, size.Maximum);
        // Without this the control *rejects* an out-of-range entry instead of clamping it, and
        // leaves the typed number in the box: the operator types 2000, sees 2000, saves, and the
        // file keeps the old value with nothing said.
        Assert.True(size.ClipValueToMinMax);

        window.Close();
    }

    [AvaloniaFact]
    public void EveryOpenSourceNoticeIsOnScreen()
    {
        var window = new SettingsWindow(
            new SettingsViewModel(new AppSettings(), () => null, openFolder: _ => { }));
        window.Show();

        var rendered = window.GetLogicalDescendants().OfType<TextBlock>()
            .Select(t => t.Text)
            .ToHashSet();
        Assert.All(OpenSourceNotices.All, notice => Assert.Contains(notice.Name, rendered));

        window.Close();
    }

    [AvaloniaFact]
    public void TheChangelogWindowShowsEveryReleaseAndItsChanges()
    {
        var window = new ChangelogWindow();
        window.Show();

        var rendered = window.GetLogicalDescendants().OfType<TextBlock>()
            .Select(t => t.Text)
            .ToHashSet();
        Assert.All(Changelog.Releases, release => Assert.Contains(release.Version, rendered));
        Assert.Contains(Changelog.Releases[0].Changes[0], rendered);

        window.Close();
    }

    /// <summary>
    /// The date beside a version is a build identifier, so it is ISO everywhere. Formatted against
    /// the ambient culture it followed the operator's calendar — a Thai or Umm al-Qura locale
    /// printed a year that matches nothing in the repository.
    /// </summary>
    [AvaloniaFact]
    public void TheChangelogDateIsTheSameInEveryCalendar()
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("th-TH");
            var entry = new ChangelogEntryViewModel(
                new ChangelogRelease("9.9.9", new DateOnly(2026, 8, 4), ["something"]));

            Assert.Equal("2026-08-04", entry.Date);
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }
}
