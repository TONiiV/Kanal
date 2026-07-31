using Kanal.Host.Services;
using Kanal.Host.ViewModels;
using Kanal.Providers.LocalMt;

namespace Kanal.Tests;

public class TranslationModelSettingsTests
{
    [Fact]
    public void CloudIsTheDefaultActiveChoice()
    {
        var vm = new SettingsViewModel(new AppSettings());

        Assert.Equal(LocalModelCatalog.Models.Count + 1, vm.TranslationModels.Count);
        var cloud = vm.TranslationModels[0];
        Assert.False(cloud.IsLocal);
        Assert.True(cloud.IsActive);
        Assert.All(vm.TranslationModels.Skip(1), m => Assert.True(m.IsLocal));
    }

    [Fact]
    public void StoredSelectionIsRestored()
    {
        var vm = new SettingsViewModel(new AppSettings { ActiveTranslationModelId = "qwen3.5-4b" });

        var active = Assert.Single(vm.TranslationModels, m => m.IsActive);
        Assert.Equal("qwen3.5-4b", active.ModelId);
    }

    [Fact]
    public void UnknownStoredSelectionFallsBackToCloud()
    {
        var vm = new SettingsViewModel(new AppSettings { ActiveTranslationModelId = "gone-model" });

        var active = Assert.Single(vm.TranslationModels, m => m.IsActive);
        Assert.False(active.IsLocal);
    }

    [Fact]
    public void ApplyToPersistsTheSelectedModel()
    {
        var vm = new SettingsViewModel(new AppSettings());
        var qwen = vm.TranslationModels.First(m => m.ModelId == "qwen3.5-4b");
        foreach (var m in vm.TranslationModels)
            m.IsActive = ReferenceEquals(m, qwen);

        var settings = new AppSettings();
        vm.ApplyTo(settings);
        Assert.Equal("qwen3.5-4b", settings.ActiveTranslationModelId);

        foreach (var m in vm.TranslationModels)
            m.IsActive = !m.IsLocal;
        vm.ApplyTo(settings);
        Assert.Null(settings.ActiveTranslationModelId);
    }

    [Fact]
    public void StatusLabelTracksDownloadLifecycle()
    {
        var downloads = new ModelDownloadManager(
            Path.Combine(Path.GetTempPath(), "kanal-tests", Guid.NewGuid().ToString("N")));
        var item = new TranslationModelItemViewModel(LocalModelCatalog.Models[0], downloads);

        Assert.Equal("not downloaded", item.StatusLabel);

        item.IsDownloading = true;
        item.Progress = 0.42;
        Assert.Equal("downloading 42%", item.StatusLabel);

        item.IsDownloading = false;
        item.IsDownloaded = true;
        Assert.Equal("downloaded", item.StatusLabel);
    }

    [Fact]
    public void CloudRowHasNoDownloadControls()
    {
        var cloud = new SettingsViewModel(new AppSettings()).TranslationModels[0];
        Assert.False(cloud.CanDownload);
        Assert.False(cloud.CanDelete);
        Assert.Equal("", cloud.StatusLabel);
    }

    [Fact]
    public void LicenseNoteSurfacesForNonPermissiveModels()
    {
        var vm = new SettingsViewModel(new AppSettings());
        var gemma = vm.TranslationModels.First(m => m.ModelId == "gemma-3-4b");
        Assert.True(gemma.HasLicenseNote);
        var qwen = vm.TranslationModels.First(m => m.ModelId == "qwen3.5-4b");
        Assert.False(qwen.HasLicenseNote);
    }
}
