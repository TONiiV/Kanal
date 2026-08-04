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
        var window = new SettingsWindow
        {
            DataContext = new SettingsViewModel(
                new AppSettings { LogLevel = LogLevel.Error, LogMaxFileSizeMb = 33 },
                () => null,
                isMacOs: false,
                deviceWatcherFactory: null,
                openFolder: _ => { }),
        };
        window.Show();

        var levels = window.GetLogicalDescendants().OfType<ComboBox>()
            .Single(c => c.SelectedItem is LogLevelOption);
        Assert.Equal(LogLevel.Error, ((LogLevelOption)levels.SelectedItem!).Level);

        // decimal? on the control, int in the settings file: the conversion happens in the
        // binding, which is exactly the sort of thing that only shows up at runtime
        var size = Assert.Single(window.GetLogicalDescendants().OfType<NumericUpDown>());
        Assert.Equal(33m, size.Value);
        Assert.Equal(SettingsStore.MaxLogMaxFileSizeMb, size.Maximum);

        window.Close();
    }

    [AvaloniaFact]
    public void EveryOpenSourceNoticeIsOnScreen()
    {
        var window = new SettingsWindow
        {
            DataContext = new SettingsViewModel(new AppSettings(), () => null, openFolder: _ => { }),
        };
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
}
