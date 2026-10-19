using System.Collections.Generic;
using System.Linq;
using Kanal.Core.Meetings;
using Kanal.Core.Providers;
using Kanal.Core.Providers.Testing;
using Kanal.Host.Localization;
using Kanal.Providers.DeepL;
using Kanal.Providers.Gladia;
using Kanal.Core.Models;
using Kanal.Providers.LocalAsr;
using Kanal.Providers.LocalMt;

namespace Kanal.Host.Services;

/// <summary>
/// A mode resolved against this machine, with nothing constructed. <see cref="Unavailable"/>
/// non-null means the mode is offered but cannot run, and says why — in the row itself, so
/// the roadmap stays visible and Start never fails with a surprise.
/// </summary>
public sealed record PipelineStatus(
    PipelineMode Mode,
    string? Unavailable,
    string TranscriptionLabel,
    string TranslationLabel);

/// <summary>
/// The provider pair a mode resolves to. <see cref="CloudTranslation"/> is the whole mechanism
/// the capability model rests on: turning the cloud provider's own translation off flips its
/// <c>Caps.Translation</c>, and that — not the mode — is what makes the orchestrator route
/// finals through <see cref="Mt"/>.
/// </summary>
/// <param name="Substitution">
/// Demo only: a note saying the chosen local model was not used, so scripted translations under
/// a model the operator believes is live are never silent.
/// </param>
public sealed record PipelinePlan(
    PipelineStatus Status,
    IAsrProvider? Asr,
    IMtProvider? Mt,
    bool CloudTranslation,
    string? Substitution,
    IMeetingTitler? Titler);

/// <summary>
/// Resolves a mode to a provider pair. This is the only place a mode is inspected — the
/// orchestrator sees providers and capabilities, never a mode or a vendor. All failure
/// modes are values, not throws.
/// </summary>
public static class PipelinePlanner
{
    /// <summary>Seam for tests: the real one reads the ambient GLADIA_API_KEY env var.</summary>
    public delegate (string Key, string? Name)? KeyResolver(AppSettings settings);

    private static Localizer L => Localizer.Instance;

    public static PipelineStatus Describe(
        PipelineMode mode, AppSettings settings, ModelDownloadManager downloads, KeyResolver? key = null,
        KeyResolver? translationKey = null) =>
        Resolve(mode, settings, downloads, key ?? SettingsStore.ResolveGladiaKey,
            translationKey ?? SettingsStore.ResolveDeepLKey).Status;

    public static PipelinePlan Plan(
        PipelineMode mode, AppSettings settings, ModelDownloadManager downloads, KeyResolver? key = null,
        KeyResolver? translationKey = null)
    {
        var resolved = Resolve(mode, settings, downloads, key ?? SettingsStore.ResolveGladiaKey,
            translationKey ?? SettingsStore.ResolveDeepLKey);
        if (resolved.Status.Unavailable is not null)
            return new PipelinePlan(resolved.Status, null, null, false, null, null);

        var asr = mode.Transcription switch
        {
            StageKind.Cloud => new GladiaAsrProvider(new GladiaOptions
            {
                ApiKey = resolved.Key!,
                // with a local model active the cloud provider stops translating, its caps drop
                // Translation, and the orchestrator picks up the IMtProvider — no special casing
                EnableTranslation = mode.Translation == StageKind.Cloud,
            }),
            StageKind.Local => new NemotronAsrProvider(resolved.Transcriber!, downloads),
            _ => (IAsrProvider)new FakeAsrProvider(loop: true),
        };

        // Demo must always run: a model that was chosen but never downloaded falls back to the
        // scripted translator rather than blocking, and Substitution says so out loud.
        // Translation and titling share one generator, and so one loaded copy of the weights:
        // a second LlamaSharpTextGenerator over the same file is a second multi-gigabyte model.
        var generator = resolved.Model is not null
            ? new LlamaSharpTextGenerator(downloads.GetPath(resolved.Model), resolved.Model.AssistantPrefill)
            : null;
        IMtProvider? mt = generator is not null
            ? new LlamaSharpMtProvider(generator)
            : mode.Translation == StageKind.Cloud
                ? resolved.TranslationKey is { } deepl ? new DeepLMtProvider(deepl) : null
                : new FakeMtProvider();

        return new PipelinePlan(
            resolved.Status,
            asr,
            mt,
            mode.Translation == StageKind.Cloud,
            resolved.Substitution,
            generator is null ? null : new GeneratedMeetingTitler(generator));
    }

    private sealed record Resolution(
        PipelineStatus Status, string? Key, AsrModelInfo? Transcriber, LocalModelInfo? Model, string? Substitution,
        string? TranslationKey);

    private static Resolution Resolve(
        PipelineMode mode, AppSettings settings, ModelDownloadManager downloads, KeyResolver key,
        KeyResolver translationKey)
    {
        var reasons = new List<string>();

        var (transcriptionLabel, apiKey, transcriber) =
            ResolveTranscription(mode, settings, downloads, key, reasons);
        var (translationLabel, model, substitution, mtKey) =
            ResolveTranslation(mode, settings, downloads, translationKey, reasons);

        var status = new PipelineStatus(
            mode,
            reasons.Count == 0 ? null : string.Join("; ", reasons),
            transcriptionLabel,
            translationLabel);
        return new Resolution(status, apiKey, transcriber, model, substitution, mtKey);
    }

    private static (string Label, string? Key, AsrModelInfo? Model) ResolveTranscription(
        PipelineMode mode, AppSettings settings, ModelDownloadManager downloads, KeyResolver key,
        List<string> reasons)
    {
        switch (mode.Transcription)
        {
            case StageKind.Scripted:
                return (L["stage.transcription.scripted"], null, null);

            case StageKind.Local:
                // nothing chosen, or an id the catalog no longer lists, is the recommended model
                var model = (settings.ActiveTranscriptionModelId is { } id ? AsrModelCatalog.Find(id) : null)
                    ?? AsrModelCatalog.Models[0];

                if (!downloads.IsDownloaded(model.Parts))
                {
                    reasons.Add(L.Format("reason.notdownloaded", model.DisplayName));
                    return (L.Format("stage.transcription.notdownloaded", model.DisplayName), null, null);
                }

                return (L.Format("stage.transcription.local", model.DisplayName), null, model);

            default:
                var resolved = key(settings);
                if (resolved is null)
                {
                    reasons.Add(L["reason.nokey"]);
                    return (L["stage.transcription.nokey"], null, null);
                }

                // the provider is named in Settings and only there; the masthead names the key
                var label = resolved.Value.Name is { } name
                    ? L.Format("stage.transcription.named", name)
                    : L["stage.transcription.env"];
                return (label, resolved.Value.Key, null);
        }
    }

    private static (string Label, LocalModelInfo? Model, string? Substitution, string? Key) ResolveTranslation(
        PipelineMode mode, AppSettings settings, ModelDownloadManager downloads, KeyResolver translationKey,
        List<string> reasons)
    {
        if (mode.Translation == StageKind.Cloud)
        {
            if (mode.Transcription == StageKind.Cloud)
                return (L["stage.translation.cloud"], null, null, null);

            if (translationKey(settings) is not { } resolved)
            {
                reasons.Add(L["reason.nomtkey"]);
                return (L["stage.translation.nokey"], null, null, null);
            }

            return (L["stage.translation.cloud"], null, null, resolved.Key);
        }

        // A scripted pipeline translates with whatever is on this machine: a downloaded model if
        // one is chosen (the only way to rehearse it without a key), scripted lines otherwise.
        var optional = mode.Translation == StageKind.Scripted;
        var id = settings.ActiveTranslationModelId;

        if (id is null)
        {
            if (optional)
                return (L["stage.translation.scripted"], null, null, null);
            reasons.Add(L["reason.nomodel"]);
            return (L["stage.translation.nomodel"], null, null, null);
        }

        var model = LocalModelCatalog.Find(id);
        if (model is null)
        {
            var reason = L.Format("reason.unknownmodel", id);
            var label = L.Format("stage.translation.unknown", id);
            if (optional)
                return (label, null, L.Format("reason.substitute", reason), null);
            reasons.Add(reason);
            return (label, null, null, null);
        }

        if (!downloads.IsDownloaded(model))
        {
            var reason = L.Format("reason.notdownloaded", model.DisplayName);
            var label = L.Format("stage.translation.notdownloaded", model.DisplayName);
            if (optional)
                return (label, null, L.Format("reason.substitute", reason), null);
            reasons.Add(reason);
            return (label, null, null, null);
        }

        return (L.Format("stage.translation.local", model.DisplayName), model, null, null);
    }
}
