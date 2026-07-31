using Kanal.Host.Services;
using Kanal.Providers.LocalMt;

namespace Kanal.Tests;

public class TranslationPlannerTests
{
    private static (ModelDownloadManager Downloads, string Dir) TempDownloads()
    {
        var dir = Path.Combine(Path.GetTempPath(), "kanal-planner-" + Guid.NewGuid().ToString("N"));
        return (new ModelDownloadManager(dir), dir);
    }

    [Fact]
    public void NoActiveModelMeansCloudTranslation()
    {
        var (downloads, _) = TempDownloads();

        var plan = TranslationPlanner.Plan(new AppSettings(), downloads);

        Assert.True(plan.CloudTranslation);
        Assert.Null(plan.Mt);
        Assert.Null(plan.Error);
    }

    [Fact]
    public void UnknownModelIdSurfacesAnError()
    {
        var (downloads, _) = TempDownloads();
        var settings = new AppSettings { ActiveTranslationModelId = "no-such-model" };

        var plan = TranslationPlanner.Plan(settings, downloads);

        Assert.NotNull(plan.Error);
        Assert.Null(plan.Mt);
    }

    [Fact]
    public void SelectedButNotDownloadedPointsAtSettings()
    {
        var (downloads, _) = TempDownloads();
        var settings = new AppSettings { ActiveTranslationModelId = LocalModelCatalog.Models[0].Id };

        var plan = TranslationPlanner.Plan(settings, downloads);

        Assert.NotNull(plan.Error);
        Assert.Contains("Settings", plan.Error);
        Assert.Null(plan.Mt);
    }

    /// <summary>
    /// The masthead label. Nothing else on the main screen says which engine will translate,
    /// so every branch — including the ones that fall back — has to name itself.
    /// </summary>
    [Fact]
    public void DescribeNamesTheEngineForEveryOutcome()
    {
        var (downloads, dir) = TempDownloads();
        var model = LocalModelCatalog.Models[0];

        Assert.Equal("Translation: Gladia (cloud)",
            TranslationPlanner.Describe(new AppSettings(), downloads));

        Assert.Equal($"Translation: {model.DisplayName} — not downloaded",
            TranslationPlanner.Describe(
                new AppSettings { ActiveTranslationModelId = model.Id }, downloads));

        Assert.Equal("Translation: unknown model \"gone-model\"",
            TranslationPlanner.Describe(
                new AppSettings { ActiveTranslationModelId = "gone-model" }, downloads));

        Directory.CreateDirectory(dir);
        File.WriteAllBytes(downloads.GetPath(model), [0x47, 0x47, 0x55, 0x46]);
        Assert.Equal($"Translation: {model.DisplayName} (local)",
            TranslationPlanner.Describe(
                new AppSettings { ActiveTranslationModelId = model.Id }, downloads));

        Directory.Delete(dir, recursive: true);
    }

    /// <summary>Describe must not build a provider — the main window calls it before Start,
    /// and constructing one is a step towards loading a multi-gigabyte model.</summary>
    [Fact]
    public void PlanCarriesTheSameDescription()
    {
        var (downloads, _) = TempDownloads();
        var settings = new AppSettings();

        Assert.Equal(TranslationPlanner.Describe(settings, downloads),
            TranslationPlanner.Plan(settings, downloads).Description);
    }

    [Fact]
    public void DownloadedModelYieldsLocalProviderAndDisablesCloudTranslation()
    {
        var (downloads, dir) = TempDownloads();
        var model = LocalModelCatalog.Models[0];
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(downloads.GetPath(model), [0x47, 0x47, 0x55, 0x46]);

        var settings = new AppSettings { ActiveTranslationModelId = model.Id };
        var plan = TranslationPlanner.Plan(settings, downloads);

        Assert.Null(plan.Error);
        Assert.False(plan.CloudTranslation);
        Assert.IsType<LlamaSharpMtProvider>(plan.Mt);
        (plan.Mt as IDisposable)?.Dispose();
        Directory.Delete(dir, recursive: true);
    }
}
