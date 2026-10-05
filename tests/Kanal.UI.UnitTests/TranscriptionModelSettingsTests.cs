using Kanal.Core.Models;
using Kanal.Host.Services;
using Kanal.Host.ViewModels;
using Kanal.Providers.LocalAsr;

namespace Kanal.UI.UnitTests;

public class TranscriptionModelSettingsTests
{
    private static string TempDir() =>
        Path.Combine(Path.GetTempPath(), "kanal-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void EveryCatalogModelIsListedAndTheRecommendedOneIsActiveByDefault()
    {
        var vm = new SettingsViewModel(new AppSettings(), () => null);

        Assert.Equal(AsrModelCatalog.Models.Select(m => m.Id), vm.TranscriptionModels.Select(m => m.ModelId));
        Assert.All(vm.TranscriptionModels, m => Assert.True(m.IsLocal));
        var active = Assert.Single(vm.TranscriptionModels, m => m.IsActive);
        Assert.Equal(AsrModelCatalog.Models[0].Id, active.ModelId);
    }

    [Fact]
    public void StoredSelectionIsRestored()
    {
        var id = AsrModelCatalog.Models[^1].Id;
        var vm = new SettingsViewModel(new AppSettings { ActiveTranscriptionModelId = id }, () => null);

        Assert.Equal(id, Assert.Single(vm.TranscriptionModels, m => m.IsActive).ModelId);
    }

    [Fact]
    public void UnknownStoredSelectionFallsBackToTheRecommendedModel()
    {
        var vm = new SettingsViewModel(new AppSettings { ActiveTranscriptionModelId = "gone" }, () => null);

        Assert.Equal(
            AsrModelCatalog.Models[0].Id, Assert.Single(vm.TranscriptionModels, m => m.IsActive).ModelId);
    }

    [Fact]
    public void ApplyToPersistsTheSelectedModel()
    {
        var vm = new SettingsViewModel(new AppSettings(), () => null);
        var pick = vm.TranscriptionModels[^1];
        foreach (var m in vm.TranscriptionModels)
            m.IsActive = ReferenceEquals(m, pick);

        var settings = new AppSettings();
        vm.ApplyTo(settings);

        Assert.Equal(pick.ModelId, settings.ActiveTranscriptionModelId);
    }

    [Fact]
    public void AModelIsDownloadedOnlyWhenEveryPartIsOnDisk()
    {
        var dir = TempDir();
        Directory.CreateDirectory(dir);
        var model = AsrModelCatalog.Models[0];
        var downloads = new ModelDownloadManager(dir);
        foreach (var part in model.Parts.Skip(1))
            File.WriteAllText(downloads.GetPath(part), "");

        Assert.False(new ModelItemViewModel(model, downloads).IsDownloaded);

        File.WriteAllText(downloads.GetPath(model.Parts[0]), "");
        Assert.True(new ModelItemViewModel(model, downloads).IsDownloaded);

        Directory.Delete(dir, recursive: true);
    }

    [Fact]
    public void DeleteRemovesEveryPart()
    {
        var dir = TempDir();
        Directory.CreateDirectory(dir);
        var model = AsrModelCatalog.Models[0];
        var downloads = new ModelDownloadManager(dir);
        foreach (var part in model.Parts)
            File.WriteAllText(downloads.GetPath(part), "");
        var item = new ModelItemViewModel(model, downloads);

        item.DeleteCommand.Execute(null);

        Assert.False(item.IsDownloaded);
        Assert.Empty(Directory.EnumerateFiles(dir));
        Directory.Delete(dir, recursive: true);
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public List<string> Requested { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requested.Add(request.RequestUri!.ToString());
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.NotFound));
        }
    }

    [Fact]
    public async Task ADownloadResumesWithTheMissingPartsRatherThanRefetchingTheEncoder()
    {
        var dir = TempDir();
        Directory.CreateDirectory(dir);
        var model = AsrModelCatalog.Models[0];
        var handler = new RecordingHandler();
        var downloads = new ModelDownloadManager(dir, new HttpClient(handler));
        File.WriteAllText(downloads.GetPath(model.Parts[0]), "");
        var item = new ModelItemViewModel(model, downloads);

        await item.DownloadCommand.ExecuteAsync(null);

        Assert.Equal(model.Parts[1].DownloadUrl, Assert.Single(handler.Requested));
        Assert.NotEqual("", item.Error);
        Directory.Delete(dir, recursive: true);
    }

    [Fact]
    public void TheRowNamesSizeAndLicenceAndCarriesTheLicenceNote()
    {
        var item = new ModelItemViewModel(AsrModelCatalog.Models[0], new ModelDownloadManager(TempDir()));

        Assert.Equal("0.6B · int8 · 560 ms · 0.6 GB · OpenMDW-1.1", item.MetaLabel);
        Assert.True(item.HasLicenseNote);
    }

    [Fact]
    public void TranscriptionAndTranslationRowsAreSeparateRadioGroupsWithTheirOwnTips()
    {
        var vm = new SettingsViewModel(new AppSettings(), () => null);
        var asr = vm.TranscriptionModels[0];
        var mt = vm.TranslationModels[1];

        Assert.NotEqual(asr.RadioGroup, mt.RadioGroup);
        Assert.Equal(mt.RadioGroup, vm.TranslationModels[0].RadioGroup);
        Assert.Equal("Use for transcription", asr.UseTip);
        Assert.Equal("Use for translation", mt.UseTip);
    }
}
