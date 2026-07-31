using Kanal.Core.Providers;
using Kanal.Providers.LocalMt;

namespace Kanal.Host.Services;

/// <summary>
/// How this session translates. Exactly one of three shapes: cloud (Gladia translates),
/// local (<see cref="Mt"/> set, cloud translation off), or <see cref="Error"/>.
/// </summary>
public sealed record TranslationPlan(bool CloudTranslation, IMtProvider? Mt, string? Error);

/// <summary>Maps the settings choice to a translation plan; all failure modes are values, not throws.</summary>
public static class TranslationPlanner
{
    public static TranslationPlan Plan(AppSettings settings, ModelDownloadManager downloads)
    {
        var id = settings.ActiveTranslationModelId;
        if (id is null)
            return new TranslationPlan(CloudTranslation: true, Mt: null, Error: null);

        var model = LocalModelCatalog.Find(id);
        if (model is null)
            return new TranslationPlan(false, null,
                $"Unknown translation model \"{id}\" — pick one in Settings.");

        if (!downloads.IsDownloaded(model))
            return new TranslationPlan(false, null,
                $"{model.DisplayName} is not downloaded yet — open Settings.");

        var generator = new LlamaSharpTextGenerator(downloads.GetPath(model));
        return new TranslationPlan(false, new LlamaSharpMtProvider(generator), null);
    }
}
