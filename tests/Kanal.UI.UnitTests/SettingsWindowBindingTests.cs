using System.Globalization;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Kanal.Core.Diagnostics;
using Kanal.Host.Localization;
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
    /// The one string the whole classification design rests on. Everything else in the panel is
    /// written in words this application chose; Debug is the setting under which the file also
    /// keeps what a transcription service and the gateway said back, which can be an utterance —
    /// and an operator handing that folder to someone has to be able to have known before they
    /// chose it. Nothing referenced this string, so deleting the line was silent.
    /// </summary>
    [AvaloniaFact]
    public void ThePanelSaysWhatDebugRecords()
    {
        var window = new SettingsWindow(
            new SettingsViewModel(new AppSettings(), () => null, openFolder: _ => { }));
        window.Show();

        var rendered = window.GetLogicalDescendants().OfType<TextBlock>()
            .Select(t => t.Text)
            .ToHashSet();
        Assert.Contains(Localizer.Instance["settings.logs.debug"], rendered);

        window.Close();
    }

    /// <summary>
    /// A Save that cannot be written. A read-only profile, a roaming-sync lock, a full disk — the
    /// throw was caught, written to a log the operator is not reading, and the dialog closed as if
    /// it had worked. They found out at the next Start, from a message about a key they had just
    /// pasted in, with nothing connecting the two.
    /// </summary>
    [AvaloniaFact]
    public void ASaveThatCannotBeWrittenKeepsTheDialogOpenAndSaysWhy()
    {
        var viewModel = new UnwritableSettingsViewModel();
        var window = new SettingsWindow(viewModel);
        window.Show();

        var save = Assert.Single(
            window.GetLogicalDescendants().OfType<Button>(), b => b.Classes.Contains("accent"));
        save.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

        Assert.True(window.IsVisible, "the dialog closed on a write that did not happen");
        Assert.Contains("read-only", viewModel.SaveError);
        // and on screen, not only on the view model
        Assert.Contains(
            window.GetLogicalDescendants().OfType<TextBlock>(),
            t => t.Text == viewModel.SaveError);

        window.Close();
    }

    /// <summary>Stands in for the profile directory nobody can write to.</summary>
    private sealed class UnwritableSettingsViewModel()
        : SettingsViewModel(new AppSettings(), () => null, openFolder: _ => { })
    {
        public override AppSettings Save() =>
            throw new UnauthorizedAccessException("The settings folder is read-only.");
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
