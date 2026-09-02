using System.Collections.Generic;
using System.Linq;
using Kanal.Core.Providers;
using Kanal.Core.Providers.Testing;
using Kanal.Host.Localization;
using Kanal.Providers.Gladia;
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
    string? Substitution);

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
        PipelineMode mode, AppSettings settings, ModelDownloadManager downloads, KeyResolver? key = null) =>
        Resolve(mode, settings, downloads, key ?? SettingsStore.ResolveGladiaKey).Status;

    public static PipelinePlan Plan(
        PipelineMode mode, AppSettings settings, ModelDownloadManager downloads, KeyResolver? key = null)
    {
        var resolved = Resolve(mode, settings, downloads, key ?? SettingsStore.ResolveGladiaKey);
        if (resolved.Status.Unavailable is not null)
            return new PipelinePlan(resolved.Status, null, null, false, null);

        var asr = mode.Transcription switch
        {
            StageKind.Cloud => new GladiaAsrProvider(new GladiaOptions
            {
                ApiKey = resolved.Key!,
                // with a local model active the cloud provider stops translating, its caps drop
                // Translation, and the orchestrator picks up the IMtProvider — no special casing
                EnableTranslation = mode.Translation == StageKind.Cloud,
            }),
            _ => (IAsrProvider)new FakeAsrProvider(loop: true),
        };

        // Demo must always run: a model that was chosen but never downloaded falls back to the
        // scripted translator rather than blocking, and Substitution says so out loud.
        var mt = resolved.Model is not null
            ? new LlamaSharpMtProvider(new LlamaSharpTextGenerator(
                downloads.GetPath(resolved.Model), resolved.Model.AssistantPrefill))
            : mode.Translation == StageKind.Cloud ? null : (IMtProvider)new FakeMtProvider();

        return new PipelinePlan(
            resolved.Status, asr, mt, mode.Translation == StageKind.Cloud, resolved.Substitution);
    }

    private sealed record Resolution(
        PipelineStatus Status, string? Key, LocalModelInfo? Model, string? Substitution);

    private static Resolution Resolve(
        PipelineMode mode, AppSettings settings, ModelDownloadManager downloads, KeyResolver key)
    {
        var reasons = new List<string>();

        var (transcriptionLabel, apiKey) = ResolveTranscription(mode, settings, key, reasons);
        var (translationLabel, model, substitution) =
            ResolveTranslation(mode, settings, downloads, reasons);

        var status = new PipelineStatus(
            mode,
            reasons.Count == 0 ? null : string.Join("; ", reasons),
            transcriptionLabel,
            translationLabel);
        return new Resolution(status, apiKey, model, substitution);
    }

    private static (string Label, string? Key) ResolveTranscription(
        PipelineMode mode, AppSettings settings, KeyResolver key, List<string> reasons)
    {
        switch (mode.Transcription)
        {
            case StageKind.Scripted:
                return (L["stage.transcription.scripted"], null);

            case StageKind.Local:
                reasons.Add(L["reason.localasr"]);
                return (L["stage.transcription.localsoon"], null);

            default:
                var resolved = key(settings);
                if (resolved is null)
                {
                    reasons.Add(L["reason.nokey"]);
                    return (L["stage.transcription.nokey"], null);
                }

                // the provider is named in Settings and only there; the masthead names the key
                var label = resolved.Value.Name is { } name
                    ? L.Format("stage.transcription.named", name)
                    : L["stage.transcription.env"];
                return (label, resolved.Value.Key);
        }
    }

    private static (string Label, LocalModelInfo? Model, string? Substitution) ResolveTranslation(
        PipelineMode mode, AppSettings settings, ModelDownloadManager downloads, List<string> reasons)
    {
        if (mode.Translation == StageKind.Cloud)
        {
            // Cloud translation exists only bundled inside the cloud ASR session; paired with a
            // local transcriber there is nothing to call. That is a missing provider, not a setting.
            if (mode.Transcription != StageKind.Cloud)
            {
                reasons.Add(L["reason.cloudmt"]);
                return (L["stage.translation.cloudsoon"], null, null);
            }

            return (L["stage.translation.cloud"], null, null);
        }

        // A scripted pipeline translates with whatever is on this machine: a downloaded model if
        // one is chosen (the only way to rehearse it without a key), scripted lines otherwise.
        var optional = mode.Translation == StageKind.Scripted;
        var id = settings.ActiveTranslationModelId;

        if (id is null)
        {
            if (optional)
                return (L["stage.translation.scripted"], null, null);
            reasons.Add(L["reason.nomodel"]);
            return (L["stage.translation.nomodel"], null, null);
        }

        var model = LocalModelCatalog.Find(id);
        if (model is null)
        {
            var reason = L.Format("reason.unknownmodel", id);
            var label = L.Format("stage.translation.unknown", id);
            if (optional)
                return (label, null, L.Format("reason.substitute", reason));
            reasons.Add(reason);
            return (label, null, null);
        }

        if (!downloads.IsDownloaded(model))
        {
            var reason = L.Format("reason.notdownloaded", model.DisplayName);
            var label = L.Format("stage.translation.notdownloaded", model.DisplayName);
            if (optional)
                return (label, null, L.Format("reason.substitute", reason));
            reasons.Add(reason);
            return (label, null, null);
        }

        return (L.Format("stage.translation.local", model.DisplayName), model, null);
    }
}
