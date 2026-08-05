using Avalonia.Headless.XUnit;
using Kanal.Core.Diagnostics;
using Kanal.Host.Diagnostics;
using Kanal.Host.Localization;
using Kanal.Host.Services;
using Kanal.Host.ViewModels;

namespace Kanal.UI.UnitTests;

/// <summary>
/// The two log choices the operator has, and the button that saves them explaining where the file
/// is over the phone. Nobody asking for a log knows what %APPDATA% means.
/// </summary>
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

    /// <summary>One click, straight to the folder — not a path the operator has to retype.</summary>
    [AvaloniaFact]
    public void OpeningTheLogFolderAsksForTheLogFolder()
    {
        var opened = new List<string>();
        var vm = Panel(new AppSettings(), opened.Add);

        vm.OpenLogFolderCommand.Execute(null);

        Assert.Equal([SettingsStore.LogsPath], opened);
    }

    /// <summary>A folder that cannot be opened is not a reason for the dialog to disappear.</summary>
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

    /// <summary>How long a file is kept — stated in the note, so the number cannot drift from it.</summary>
    private static int LogSettingsPanelRetention => Kanal.Host.Diagnostics.LogSetup.RetentionDays;

    /// <summary>
    /// The alarm line under the two controls was decided once, when the dialog opened:
    /// <c>LogIsWritable</c> announced nothing, so a Save that changed where the log goes left it
    /// saying whatever it had said before — including still promising a file after the folder
    /// stopped being writable.
    /// </summary>
    [AvaloniaFact]
    public void RereadingTheLogStateAnnouncesBothPropertiesThatMirrorIt()
    {
        var vm = Panel(new AppSettings());
        var raised = new List<string?>();
        vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        vm.RefreshLogState();

        Assert.Contains(nameof(vm.LogIsWritable), raised);
        Assert.Contains(nameof(vm.LogFailureNote), raised);
    }
    /// <summary>
    /// The operator picks the rollover size; what it costs on their disk follows from it and from
    /// the archive floor that keeps the fortnight honest. Twenty files at the largest size the box
    /// offers is twenty gigabytes on a laptop, and nothing on the panel said so — bigger sounds
    /// safer right up until it is measured.
    /// </summary>
    [AvaloniaTheory]
    [InlineData(1)]
    [InlineData(10)]
    [InlineData(SettingsStore.MaxLogMaxFileSizeMb)]
    public void TheDiskNoteFollowsTheChosenSize(int megabytes)
    {
        var vm = Panel(new AppSettings());
        vm.LogMaxFileSizeMb = megabytes;

        // derived from the same function the target is built from, so the number on screen cannot
        // drift from the number NLog is actually given
        var gigabytes = Math.Round(
            LogSetup.MaxFolderMb(new AppSettings { LogMaxFileSizeMb = megabytes }) / 1024.0);
        Assert.Contains(gigabytes.ToString("0"), vm.LogDiskNote);
    }

    /// <summary>A number that changes while the dialog is open has to take the note with it.</summary>
    [AvaloniaFact]
    public void ChangingTheSizeAnnouncesTheDiskNote()
    {
        var vm = Panel(new AppSettings());
        var raised = new List<string?>();
        vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        vm.LogMaxFileSizeMb = SettingsStore.MaxLogMaxFileSizeMb;

        Assert.Contains(nameof(vm.LogDiskNote), raised);
    }

}
