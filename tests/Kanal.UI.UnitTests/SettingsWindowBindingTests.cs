using System.Globalization;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Kanal.Host.Controls;
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

    /// <summary>
    /// The application's own language is picked by name and flag together: the flag alone would
    /// say nothing to someone who cannot read it, so the name stays beside every one.
    /// </summary>
    [AvaloniaFact]
    public void TheApplicationLanguagePickerShowsAFlagBesideTheNameOfTheChosenAndEveryOtherLanguage()
    {
        var window = new SettingsWindow(new SettingsViewModel(
            new AppSettings { AppLanguage = "de" },
            () => null,
            isMacOs: false,
            deviceWatcherFactory: null,
            openFolder: _ => { }));
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var picker = window.GetLogicalDescendants().OfType<ComboBox>().Where(c => c.Name == "AppLanguage").Distinct().Single();
        var chosen = picker.GetVisualDescendants().OfType<FlagIcon>().Single();
        Assert.Equal("de", chosen.Code);
        Assert.Contains(picker.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == "Deutsch");

        picker.IsDropDownOpen = true;
        Dispatcher.UIThread.RunJobs();

        var rows = Enumerable.Range(0, Localizer.Available.Count)
            .Select(i => Assert.IsType<ComboBoxItem>(picker.ContainerFromIndex(i)))
            .ToList();
        Assert.Equal(
            Localizer.Available.Select(l => l.Code),
            rows.Select(r => r.GetVisualDescendants().OfType<FlagIcon>().Single().Code));
        Assert.Equal(
            Localizer.Available.Select(l => l.NativeName),
            rows.Select(r => r.GetVisualDescendants().OfType<TextBlock>().Single().Text));

        picker.IsDropDownOpen = false;
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
