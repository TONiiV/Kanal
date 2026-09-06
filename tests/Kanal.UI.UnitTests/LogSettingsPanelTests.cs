using Avalonia.Headless.XUnit;
using Kanal.Core.Diagnostics;
using Kanal.Host.Localization;
using Kanal.Host.Services;
using Kanal.Host.ViewModels;

namespace Kanal.UI.UnitTests;

public class LogSettingsPanelTests
{
    private static SettingsViewModel Panel(AppSettings settings, Action<string>? openFolder = null) =>
        new(settings, () => null, isMacOs: false, deviceWatcherFactory: null, openFolder: openFolder);

    [AvaloniaFact]
    public void TheStoredLevelIsTheSelectedOne()
    {
        var vm = Panel(new AppSettings { LogLevel = LogLevel.Warning });

        Assert.Equal(LogLevel.Warning, vm.LogLevel!.Level);
        Assert.Equal(
            [LogLevel.Debug, LogLevel.Info, LogLevel.Warning, LogLevel.Error],
            vm.LogLevels.Select(o => o.Level));
    }

    [AvaloniaFact]
    public void TheLevelAndTheSizeRoundTripThroughSave()
    {
        var vm = Panel(new AppSettings());
        vm.LogLevel = vm.LogLevels.Single(o => o.Level == LogLevel.Debug);
        vm.LogMaxFileSizeMb = 42;

        var written = new AppSettings();
        vm.ApplyTo(written);

        Assert.Equal(LogLevel.Debug, written.LogLevel);
        Assert.Equal(42, written.LogMaxFileSizeMb);
    }

    [AvaloniaFact]
    public void OpeningTheLogFolderAsksForTheLogFolder()
    {
        var opened = new List<string>();
        var vm = Panel(new AppSettings(), opened.Add);

        vm.OpenLogFolderCommand.Execute(null);

        Assert.Equal([SettingsStore.LogsPath], opened);
    }

    [AvaloniaFact]
    public void AFolderThatWillNotOpenDoesNotTakeTheDialogDown()
    {
        var vm = Panel(new AppSettings(), _ => throw new InvalidOperationException("no file manager"));

        vm.OpenLogFolderCommand.Execute(null);
    }

    [AvaloniaFact]
    public void TheLogPanelFollowsTheApplicationLanguage()
    {
        var previous = Localizer.Instance.Current;
        try
        {
            Localizer.Instance.Current = "en";
            var vm = Panel(new AppSettings());
            var english = vm.LogNote;

            vm.AppLanguage = Localizer.Available.First(l => l.Code == "pl");

            var pl = Strings.Tables["pl"];
            Assert.NotEqual(english, vm.LogNote);
            Assert.Equal(
                string.Format(pl["settings.logs.note"], LogSettingsPanelRetention),
                vm.LogNote);
            Assert.Equal(pl["log.level.warning"], vm.LogLevels.Single(o => o.Level == LogLevel.Warning).Name);
        }
        finally
        {
            Localizer.Instance.Current = previous;
        }
    }

    private static int LogSettingsPanelRetention => Kanal.Host.Diagnostics.LogSetup.RetentionDays;
}
