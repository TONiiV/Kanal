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
