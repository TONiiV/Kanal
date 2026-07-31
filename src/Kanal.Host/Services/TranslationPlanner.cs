using Kanal.Core.Providers;
using Kanal.Providers.LocalMt;

namespace Kanal.Host.Services;

/// <summary>
/// How this session translates. Exactly one of three shapes: cloud (Gladia translates),
/// local (<see cref="Mt"/> set, cloud translation off), or <see cref="Error"/>.
/// <see cref="Description"/> names the engine for the operator in every one of them.
/// </summary>
public sealed record TranslationPlan(bool CloudTranslation, IMtProvider? Mt, string? Error, string Description);

/// <summary>Maps the settings choice to a translation plan; all failure modes are values, not throws.</summary>
public static class TranslationPlanner
{
    public static TranslationPlan Plan(AppSettings settings, ModelDownloadManager downloads)
    {
        var (model, error, description) = Resolve(settings, downloads);

        if (model is null || error is not null)
            return new TranslationPlan(
                CloudTranslation: model is null && error is null,
                Mt: null,
                error,
                description);

        var generator = new LlamaSharpTextGenerator(downloads.GetPath(model));
        return new TranslationPlan(false, new LlamaSharpMtProvider(generator), null, description);
    }

    /// <summary>
    /// The engine label on its own. No provider is constructed, so the main window can keep
    /// this current without taking a step towards loading a multi-gigabyte model.
    /// </summary>
    public static string Describe(AppSettings settings, ModelDownloadManager downloads) =>
        Resolve(settings, downloads).Description;

    private static (LocalModelInfo? Model, string? Error, string Description) Resolve(
        AppSettings settings, ModelDownloadManager downloads)
    {
        var id = settings.ActiveTranslationModelId;
        if (id is null)
            return (null, null, "Translation: Gladia (cloud)");

        var model = LocalModelCatalog.Find(id);
        if (model is null)
            return (null,
                $"Unknown translation model \"{id}\" — pick one in Settings.",
                $"Translation: unknown model \"{id}\"");

        if (!downloads.IsDownloaded(model))
            return (model,
                $"{model.DisplayName} is not downloaded yet — open Settings.",
                $"Translation: {model.DisplayName} — not downloaded");

        return (model, null, $"Translation: {model.DisplayName} (local)");
    }
}
