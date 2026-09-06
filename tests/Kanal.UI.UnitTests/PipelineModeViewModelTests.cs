using Avalonia.Headless.XUnit;
using Kanal.Host.Services;
using Kanal.Host.ViewModels;

namespace Kanal.UI.UnitTests;

/// <summary>
/// The state exposed by the mode selector: all pipelines remain discoverable, unavailable modes
/// explain why they cannot run, and selecting a mode updates the stage status.
/// </summary>
public class PipelineModeViewModelTests
{
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
    public async Task StartRefusesAModeThatCannotRunAndSaysWhy()
    {
        var vm = TestViewModels.Hermetic();
        var unavailable = vm.Modes.First(o => o.Mode.Id == PipelineModeId.LocalLocal);
        vm.SelectedMode = unavailable;
        vm.ConsentConfirmed = true;

        await vm.StartCommand.ExecuteAsync(null);

        Assert.False(vm.IsRunning);
        Assert.Equal(unavailable.Unavailable, vm.Status);
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

}
