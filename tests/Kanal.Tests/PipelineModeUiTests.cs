using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Kanal.Host.Services;
using Kanal.Host.ViewModels;
using Kanal.Host.Views;
using Kanal.Providers.LocalMt;

namespace Kanal.Tests;

/// <summary>
/// The mode dropdown as the operator meets it: five pipelines, the unavailable ones visible
/// and disabled with the reason printed beside them, and no company name anywhere on screen.
/// </summary>
public class PipelineModeUiTests
{
    private static ComboBox ModeCombo(MainWindow window) =>
        window.GetVisualDescendants().OfType<ComboBox>()
            .First(c => c.ItemsSource is IEnumerable<PipelineModeOption>);

    [AvaloniaFact]
    public void AllFiveModesAreOfferedAndNamedAfterTheirPipeline()
    {
        var vm = TestViewModels.Hermetic();

        Assert.Equal(PipelineMode.All.Count, vm.Modes.Count);
        Assert.Equal(PipelineModeId.Demo, vm.SelectedMode.Mode.Id);
        Assert.Equal(PipelineMode.All.Select(m => m.Name), vm.Modes.Select(o => o.Name));
    }

    /// <summary>Hiding them hides the roadmap; offering them and failing at Start is worse.</summary>
    [AvaloniaFact]
    public void UnavailableModesStayVisibleAndCarryTheirReason()
    {
        var vm = TestViewModels.Hermetic();

        foreach (var option in vm.Modes.Where(o => !o.IsAvailable))
        {
            Assert.False(string.IsNullOrWhiteSpace(option.Unavailable));
            // the reason sits in the row itself, next to what the mode would send out
            Assert.Contains(option.Unavailable!, option.Detail, StringComparison.Ordinal);
            Assert.Contains(option.Mode.Leaves, option.Detail, StringComparison.Ordinal);
        }

        // both local-transcription rows, at least, cannot run yet
        Assert.Contains(vm.Modes, o => o.Mode.Id == PipelineModeId.LocalCloud && !o.IsAvailable);
        Assert.Contains(vm.Modes, o => o.Mode.Id == PipelineModeId.LocalLocal && !o.IsAvailable);
    }

    /// <summary>
    /// Availability was carried only by the row's contrast, which is the same signal a
    /// long second line of grey text already uses — at a glance the list read as five equal
    /// choices. Each row now states its status in words as well.
    /// </summary>
    [AvaloniaFact]
    public void EveryRowSaysWhetherItCanRun()
    {
        var vm = TestViewModels.Hermetic();

        foreach (var option in vm.Modes)
        {
            Assert.False(string.IsNullOrWhiteSpace(option.Status));
            if (option.IsAvailable)
                Assert.Equal("ready", option.Status);
            else
                Assert.Equal(option.Unavailable, option.Status);
        }

        Assert.Contains(vm.Modes, o => o.Status == "ready");
        Assert.Contains(vm.Modes, o => o.Status != "ready");
    }

    /// <summary>The status has to follow the settings it describes, not the value it was born with.</summary>
    [AvaloniaFact]
    public void StatusFollowsTheSettingsItDescribes()
    {
        var settings = new AppSettings();
        var vm = TestViewModels.Hermetic(settings);
        var cloud = vm.Modes.First(o => o.Mode.Id == PipelineModeId.CloudCloud);

        // Without this the test is vacuous on any machine whose environment carries the key:
        // the mode is available from construction and the refresh below proves nothing.
        Assert.False(cloud.IsAvailable);

        settings.ApiKeys.Add(new ApiKeyEntry("meeting-room", "gladia", "k"));
        vm.RefreshPipelineStatus();

        Assert.True(cloud.IsAvailable);
        Assert.Equal("ready", cloud.Status);
    }

    [AvaloniaFact]
    public void EveryModeRowStatesWhatLeavesTheMachine()
    {
        var vm = TestViewModels.Hermetic();

        foreach (var option in vm.Modes)
            Assert.Contains(option.Mode.Leaves, option.Detail, StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public void DropdownContainersForUnavailableModesAreDisabled()
    {
        var vm = TestViewModels.Hermetic();
        var window = new MainWindow { DataContext = vm };
        window.Show();

        var combo = ModeCombo(window);
        combo.IsDropDownOpen = true;
        Dispatcher.UIThread.RunJobs();

        var checkedAny = false;
        for (var i = 0; i < vm.Modes.Count; i++)
        {
            if (combo.ContainerFromIndex(i) is not ComboBoxItem container)
                continue;
            checkedAny = true;
            Assert.Equal(vm.Modes[i].IsAvailable, container.IsEnabled);
        }

        Assert.True(checkedAny, "no ComboBoxItem containers were realised — the test proves nothing.");

        combo.IsDropDownOpen = false;
        window.Close();
    }

    [AvaloniaFact]
    public async Task StartRefusesAModeThatCannotRunAndSaysWhy()
    {
        var vm = TestViewModels.Hermetic();
        var unavailable = vm.Modes.First(o => o.Mode.Id == PipelineModeId.LocalLocal);
        vm.SelectedMode = unavailable;

        await vm.StartCommand.ExecuteAsync(null);

        Assert.False(vm.IsRunning);
        Assert.Equal(unavailable.Unavailable, vm.Status);
    }

    [AvaloniaFact]
    public void MastheadNamesBothStages()
    {
        var vm = TestViewModels.Hermetic();
        var window = new MainWindow { DataContext = vm };
        window.Show();

        var texts = window.GetVisualDescendants().OfType<TextBlock>()
            .Select(t => t.Text).Where(t => t is not null).ToList();

        Assert.Contains("Transcription: scripted", texts);
        Assert.Contains("Translation: scripted", texts);

        window.Close();
    }

    [AvaloniaFact]
    public void SwitchingModeRepointsBothStageLabels()
    {
        var settings = new AppSettings();
        settings.ApiKeys.Add(new ApiKeyEntry("meeting-room", "gladia", "k"));
        settings.ActiveGladiaKeyName = "meeting-room";
        var vm = TestViewModels.Hermetic(settings);

        vm.SelectedMode = vm.Modes.First(o => o.Mode.Id == PipelineModeId.CloudCloud);

        Assert.Contains("meeting-room", vm.TranscriptionStatus);
        Assert.Equal("Translation: cloud", vm.TranslationStatus);

        vm.SelectedMode = vm.Modes.First(o => o.Mode.Id == PipelineModeId.Demo);
        Assert.Equal("Transcription: scripted", vm.TranscriptionStatus);
        Assert.Equal("Translation: scripted", vm.TranslationStatus);
    }

    /// <summary>
    /// The whole point of #14: the operator should not have to know which company transcribes
    /// their meeting in order to start it. Nothing on the main screen — including the opened
    /// mode dropdown — may name one.
    /// </summary>
    [AvaloniaFact]
    public void NothingOnTheMainScreenNamesAVendor()
    {
        var vm = TestViewModels.Hermetic();
        var window = new MainWindow { DataContext = vm };
        window.Show();

        var combo = ModeCombo(window);
        combo.IsDropDownOpen = true;
        Dispatcher.UIThread.RunJobs();

        var texts = window.GetVisualDescendants().OfType<TextBlock>()
            .Select(t => t.Text)
            .Concat(vm.Modes.Select(o => o.Name))
            .Concat(vm.Modes.Select(o => o.Detail))
            .Where(t => !string.IsNullOrEmpty(t))
            .ToList();

        foreach (var text in texts)
        foreach (var vendor in PipelineModeTests.VendorNames)
            Assert.DoesNotContain(vendor, text!, StringComparison.OrdinalIgnoreCase);

        combo.IsDropDownOpen = false;
        window.Close();
    }

    /// <summary>
    /// Settings is the one place a vendor is genuinely the subject — it is where the operator
    /// types that vendor's key — but the translation stage there is about a local model file,
    /// so the section is grouped by stage, not by company.
    /// </summary>
    [AvaloniaFact]
    public void SettingsIsGroupedByStage()
    {
        var window = new SettingsWindow { DataContext = new SettingsViewModel(new AppSettings()) };
        window.Show();

        var texts = window.GetVisualDescendants().OfType<TextBlock>()
            .Select(t => t.Text).Where(t => t is not null).ToList();

        Assert.Contains("TRANSCRIPTION", texts);
        Assert.Contains("TRANSLATION", texts);
        foreach (var model in LocalModelCatalog.Models)
            Assert.Contains(model.DisplayName, texts);

        window.Close();
    }
}
