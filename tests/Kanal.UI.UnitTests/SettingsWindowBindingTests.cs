using System.Globalization;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using Kanal.Core.Diagnostics;
using Kanal.Host.Localization;
using Kanal.Host.Services;
using Kanal.Host.ViewModels;
using Kanal.Host.Views;

namespace Kanal.UI.UnitTests;

public class SettingsWindowBindingTests
{
    [AvaloniaFact]
    public void TheLogPanelBindsToTheChosenLevelAndSize()
    {
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

        var size = Assert.Single(window.GetLogicalDescendants().OfType<NumericUpDown>());
        Assert.Equal(33m, size.Value);
        Assert.Equal(SettingsStore.MaxLogMaxFileSizeMb, size.Maximum);
        Assert.True(size.ClipValueToMinMax);

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

    [AvaloniaFact]
    public void TheOpenSourceWindowShowsEveryProjectAndItsLicence()
    {
        var window = new OpenSourceWindow();
        window.Show();

        var rendered = window.GetLogicalDescendants().OfType<TextBlock>()
            .Select(t => t.Text)
            .ToHashSet();
        Assert.Equal(Localizer.Instance["licenses.title"], window.Title);
        Assert.Contains(Localizer.Instance.Format("licenses.note", OpenSourceNotices.OwnLicense), rendered);
        Assert.Contains(window.GetLogicalDescendants().OfType<Button>(),
            button => Equals(button.Content, Localizer.Instance["licenses.close"]));
        Assert.All(OpenSourceNotices.All, notice =>
        {
            Assert.Contains(notice.Name, rendered);
            Assert.Contains(notice.License, rendered);
            Assert.Contains(notice.Url, rendered);
        });

        window.Close();
    }

    [AvaloniaFact]
    public void SettingsLinksToOpenSourceNoticesInsteadOfEmbeddingThem()
    {
        var window = new SettingsWindow(new SettingsViewModel(
            new AppSettings(),
            () => null,
            isMacOs: false,
            deviceWatcherFactory: null,
            openFolder: _ => { }));
        window.Show();

        var openLabel = Localizer.Instance["settings.licenses.open"];
        Assert.NotEqual("settings.licenses.open", openLabel);
        Assert.Contains(window.GetLogicalDescendants().OfType<Button>(),
            button => Equals(button.Content, openLabel));
        Assert.DoesNotContain(window.GetLogicalDescendants().OfType<TextBlock>(),
            text => text.Text == "Avalonia");

        window.Close();
    }

    [AvaloniaFact]
    public void AVersionThatIsNotOutYetSaysSoRatherThanShowingAnEmptyDate()
    {
        var entry = new ChangelogEntryViewModel(new ChangelogRelease("1.0.1", null, ["something"]));

        Assert.Equal(Localizer.Instance["changelog.unreleased"], entry.Date);
        Assert.NotEqual("changelog.unreleased", entry.Date);
    }

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
