using Avalonia.Headless.XUnit;
using Kanal.Host.Services;
using Kanal.Host.ViewModels;
using Kanal.Providers.LocalMt;

namespace Kanal.UI.UnitTests;

/// <summary>
/// Builds view models that never read the developer's real %APPDATA%\Kanal\settings.json.
/// Without this a headless test run on a machine with a downloaded model would load a
/// multi-gigabyte LLM, and its result would depend on whose machine it ran on.
/// </summary>
internal static class TestViewModels
{
    /// <summary>A models directory that is guaranteed to hold nothing.</summary>
    internal static string EmptyModelsDir() =>
        Path.Combine(Path.GetTempPath(), "kanal-no-models-" + Guid.NewGuid().ToString("N"));

    internal static MainViewModel Hermetic(
        AppSettings? settings = null,
        string? modelsDir = null,
        Func<DateTimeOffset>? utcNow = null)
    {
        var resolved = settings ?? new AppSettings();
        var dir = modelsDir ?? EmptyModelsDir();
        // Stored keys only — the default resolver falls back to the ambient GLADIA_API_KEY,
        // which made "unavailable without a key" assertions vacuous on a machine that has one.
        return new MainViewModel(
            () => resolved,
            () => new ModelDownloadManager(dir),
            SettingsStore.ResolveStoredGladiaKey,
            utcNow: utcNow)
        {
            RelayEnabled = false,
        };
    }

    internal static MainViewModel Demo(AppSettings? settings = null, string? modelsDir = null)
    {
        var vm = Hermetic(settings, modelsDir);
        vm.SelectedMode = vm.Modes.First(o => o.Mode.Id == PipelineModeId.Demo);
        return vm;
    }
}

/// <summary>The translation half of the masthead's two-stage indicator.</summary>
public class TranslationStatusTests
{
    [AvaloniaFact]
    public void ScriptedIsNamedWhenDemoHasNoLocalModel()
    {
        Assert.Equal("Translation: scripted", TestViewModels.Demo().TranslationStatus);
    }

    [AvaloniaFact]
    public void ChosenLocalModelIsNamedOnceItIsDownloaded()
    {
        var model = LocalModelCatalog.Models[0];
        var dir = TestViewModels.EmptyModelsDir();
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, model.FileName), [0x47, 0x47, 0x55, 0x46]);

        var vm = TestViewModels.Demo(
            new AppSettings { ActiveTranslationModelId = model.Id }, dir);

        Assert.Equal($"Translation: {model.DisplayName} (local)", vm.TranslationStatus);
        Directory.Delete(dir, recursive: true);
    }

    /// <summary>
    /// The silent-substitution case: an operator who picked a model they never downloaded used
    /// to get scripted demo translations with nothing on screen saying their choice was inactive.
    /// </summary>
    [AvaloniaFact]
    public void AnUndownloadedModelSaysSoRatherThanLookingLikeCloud()
    {
        var model = LocalModelCatalog.Models[0];
        var vm = TestViewModels.Demo(new AppSettings { ActiveTranslationModelId = model.Id });

        Assert.Equal($"Translation: {model.DisplayName} — not downloaded", vm.TranslationStatus);
    }

    [AvaloniaFact]
    public async Task DemoModeSaysWhenItSubstitutedScriptedTranslations()
    {
        var model = LocalModelCatalog.Models[0];
        var vm = TestViewModels.Demo(new AppSettings { ActiveTranslationModelId = model.Id });

        await vm.StartCommand.ExecuteAsync(null);

        Assert.Contains("not downloaded", vm.Status);
        Assert.Contains("scripted", vm.Status);
        await vm.StopCommand.ExecuteAsync(null);
    }

    /// <summary>The transcription label reads the injected settings, not the machine's env var.</summary>
    [AvaloniaFact]
    public void TheKeyInUseIsNamedFromTheInjectedSettings()
    {
        var settings = new AppSettings();
        settings.ApiKeys.Add(new ApiKeyEntry("meeting-room", "gladia", "k"));
        settings.ActiveGladiaKeyName = "meeting-room";

        var vm = TestViewModels.Hermetic(settings);
        vm.SelectedMode = vm.Modes.First(o => o.Mode.Id == PipelineModeId.CloudCloud);

        Assert.Contains("meeting-room", vm.TranscriptionStatus);
    }

    [AvaloniaFact]
    public void RefreshPicksUpAModelChosenInSettings()
    {
        var model = LocalModelCatalog.Models[0];
        var settings = new AppSettings();
        var vm = TestViewModels.Demo(settings);
        Assert.Equal("Translation: scripted", vm.TranslationStatus);

        // what the host does after settings change
        settings.ActiveTranslationModelId = model.Id;
        vm.RefreshPipelineStatus();

        Assert.Equal($"Translation: {model.DisplayName} — not downloaded", vm.TranslationStatus);
    }

    /// <summary>A model chosen in Settings unblocks the cloud · local row without a restart.</summary>
    [AvaloniaFact]
    public void RefreshReEvaluatesEveryModesAvailability()
    {
        var model = LocalModelCatalog.Models[0];
        var dir = TestViewModels.EmptyModelsDir();
        var settings = new AppSettings();
        var vm = TestViewModels.Hermetic(settings, dir);
        var cloudLocal = vm.Modes.First(o => o.Mode.Id == PipelineModeId.CloudLocal);
        Assert.False(cloudLocal.IsAvailable);

        settings.ApiKeys.Add(new ApiKeyEntry("meeting-room", "gladia", "k"));
        settings.ActiveGladiaKeyName = "meeting-room";
        settings.ActiveTranslationModelId = model.Id;
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, model.FileName), [0x47, 0x47, 0x55, 0x46]);
        vm.RefreshPipelineStatus();

        Assert.True(cloudLocal.IsAvailable);
        Directory.Delete(dir, recursive: true);
    }
}
