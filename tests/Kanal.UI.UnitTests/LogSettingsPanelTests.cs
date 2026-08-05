using Avalonia.Headless.XUnit;
using System.Globalization;
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
    /// <remarks>
    /// The expected strings are literals, worked out by hand from
    /// <c>(max(20, 2048 / size) + 1) × size</c> rounded up to a tenth of a gigabyte — 1 MB → 2049 MB
    /// → 2.1, 10 MB → 2050 MB → 2.1, 121 MB → 2541 MB → 2.5, 512 MB → 10752 MB → 10.5,
    /// 1024 MB → 21504 MB → 21. Deriving them from <c>DiskCeilingGb</c> instead pinned only that the
    /// box reaches the function: adding 100 GB to it, so the default read "about 102.1 GB", passed
    /// the whole suite. A test that computes its expectation from the code under test agrees with
    /// whatever that code says.
    /// </remarks>
    [AvaloniaTheory]
    [InlineData(1, "2.1")]
    [InlineData(10, "2.1")]
    [InlineData(121, "2.5")]
    [InlineData(512, "10.5")]
    [InlineData(SettingsStore.MaxLogMaxFileSizeMb, "21")]
    public void TheDiskNoteFollowsTheChosenSize(int megabytes, string gigabytes)
    {
        // The separator is the operator's, the digits are not: pinned here so a de-DE run asserts
        // the same number it asserts anywhere else.
        var previous = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        try
        {
            var vm = Panel(new AppSettings());
            vm.LogMaxFileSizeMb = megabytes;

            // The whole note, not a substring of it: "2" is inside "21", so a note hard-coded to any
            // one size passed a Contains assertion at every other size.
            Assert.Equal(
                Localizer.Instance.Format("settings.logs.disk", gigabytes),
                vm.LogDiskNote);
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    /// <summary>
    /// A ceiling that is displayed lower than it is defeats the point of displaying it. Rounding to
    /// the nearest whole gigabyte showed 512 MB — a true 10.5 GB — as "10", and 121 MB as "2"
    /// against a true 2.48 GB, a fifth under. The number on screen may overstate; it may never
    /// understate.
    /// </summary>
    [AvaloniaFact]
    public void TheDiskCeilingIsNeverShownLowerThanItIs()
    {
        for (var megabytes = SettingsStore.MinLogMaxFileSizeMb;
             megabytes <= SettingsStore.MaxLogMaxFileSizeMb;
             megabytes++)
        {
            var shown = double.Parse(
                SettingsViewModel.DiskCeilingGb(megabytes), CultureInfo.CurrentCulture);
            var real = LogSetup.MaxFolderMb(new AppSettings { LogMaxFileSizeMb = megabytes }) / 1024.0;

            Assert.True(
                shown >= real,
                $"{megabytes} MB: the panel says {shown} GB where the folder can reach {real:0.###} GB");
        }
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
