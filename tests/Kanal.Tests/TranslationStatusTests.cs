using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Kanal.Host.Services;
using Kanal.Host.ViewModels;
using Kanal.Host.Views;
using Kanal.Providers.LocalMt;

namespace Kanal.Tests;

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

    internal static MainViewModel Hermetic(AppSettings? settings = null, string? modelsDir = null)
    {
        var resolved = settings ?? new AppSettings();
        var dir = modelsDir ?? EmptyModelsDir();
        return new MainViewModel(() => resolved, () => new ModelDownloadManager(dir))
        {
            RelayEnabled = false,
        };
    }
}

public class TranslationStatusTests
{
    [AvaloniaFact]
    public void CloudIsNamedWhenNoLocalModelIsSelected()
    {
        Assert.Equal("Translation: Gladia (cloud)", TestViewModels.Hermetic().TranslationStatus);
    }

    [AvaloniaFact]
    public void ChosenLocalModelIsNamedOnceItIsDownloaded()
    {
        var model = LocalModelCatalog.Models[0];
        var dir = TestViewModels.EmptyModelsDir();
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, model.FileName), [0x47, 0x47, 0x55, 0x46]);

        var vm = TestViewModels.Hermetic(
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
        var vm = TestViewModels.Hermetic(new AppSettings { ActiveTranslationModelId = model.Id });

        Assert.Equal($"Translation: {model.DisplayName} — not downloaded", vm.TranslationStatus);
    }

    [AvaloniaFact]
    public async Task DemoModeSaysWhenItSubstitutedScriptedTranslations()
    {
        var model = LocalModelCatalog.Models[0];
        var vm = TestViewModels.Hermetic(new AppSettings { ActiveTranslationModelId = model.Id });
        vm.SelectedMode = "Demo (scripted)";

        await vm.StartCommand.ExecuteAsync(null);

        Assert.Contains("not downloaded", vm.Status);
        Assert.Contains("scripted", vm.Status);
        await vm.StopCommand.ExecuteAsync(null);
    }

    [AvaloniaFact]
    public void KeyStatusComesFromTheInjectedSettingsNotTheMachine()
    {
        var settings = new AppSettings();
        settings.ApiKeys.Add(new ApiKeyEntry("meeting-room", "gladia", "k"));
        settings.ActiveGladiaKeyName = "meeting-room";

        Assert.Contains("meeting-room", TestViewModels.Hermetic(settings).KeyStatus);
    }

    /// <summary>The label has to be on the main screen, next to the key status — an operator
    /// should not have to infer the engine from translation latency.</summary>
    [AvaloniaFact]
    public void MastheadShowsTheTranslationEngineNextToTheKeyStatus()
    {
        var vm = TestViewModels.Hermetic();
        var window = new MainWindow { DataContext = vm };
        window.Show();

        var texts = window.GetVisualDescendants().OfType<TextBlock>()
            .Select(t => t.Text).Where(t => t is not null).ToList();

        Assert.Contains("Translation: Gladia (cloud)", texts);
        Assert.Contains(texts, t => t!.StartsWith("Gladia key:", StringComparison.Ordinal));

        window.Close();
    }

    [AvaloniaFact]
    public void RefreshPicksUpAModelChosenInSettings()
    {
        var model = LocalModelCatalog.Models[0];
        var settings = new AppSettings();
        var vm = TestViewModels.Hermetic(settings);
        Assert.Equal("Translation: Gladia (cloud)", vm.TranslationStatus);

        // what MainWindow does after the Settings dialog closes
        settings.ActiveTranslationModelId = model.Id;
        vm.RefreshKeyStatus();

        Assert.Equal($"Translation: {model.DisplayName} — not downloaded", vm.TranslationStatus);
    }
}
