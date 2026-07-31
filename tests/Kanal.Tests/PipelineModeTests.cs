using Kanal.Core.Providers;
using Kanal.Host.Services;
using Kanal.Providers.LocalMt;

namespace Kanal.Tests;

/// <summary>
/// The mode names a pipeline, not a vendor, and resolves to a provider pair. Every string
/// asserted here is read by an operator before a meeting with an outside supplier, so the
/// two questions it has to answer are "which engine runs each stage" and "what leaves the
/// machine" — never "which company".
/// </summary>
public class PipelineModeTests
{
    /// <summary>Service brands that used to be, or could become, the name of a control.</summary>
    internal static readonly string[] VendorNames =
        ["Gladia", "Whisper", "DeepL", "Google", "OpenAI", "Claude", "Anthropic", "Qwen", "Gemma", "Supabase"];

    private static (ModelDownloadManager Downloads, string Dir) TempDownloads()
    {
        var dir = Path.Combine(Path.GetTempPath(), "kanal-pipeline-" + Guid.NewGuid().ToString("N"));
        return (new ModelDownloadManager(dir), dir);
    }

    private static string DownloadedModelsDir(LocalModelInfo model)
    {
        var dir = Path.Combine(Path.GetTempPath(), "kanal-pipeline-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, model.FileName), [0x47, 0x47, 0x55, 0x46]);
        return dir;
    }

    /// <summary>No key at all, regardless of the developer's GLADIA_API_KEY env var.</summary>
    private static readonly PipelinePlanner.KeyResolver NoKey = _ => null;

    private static readonly PipelinePlanner.KeyResolver SomeKey = _ => ("k", "meeting-room");

    /// <summary>
    /// Every mode explains itself in the help flyout. The list is the roadmap as much as it is a
    /// control — three of five modes cannot run yet — so a row the operator cannot pick still has
    /// to say what it would do, without naming the company that would do it.
    /// </summary>
    [Fact]
    public void EveryModeExplainsItself()
    {
        var seen = new HashSet<string>();
        foreach (var mode in PipelineMode.All)
        {
            Assert.False(string.IsNullOrWhiteSpace(mode.Help), $"{mode.Id} has no help text.");
            Assert.True(mode.Help.Length > 40, $"{mode.Id} help is too thin to be worth a flyout.");
            Assert.True(seen.Add(mode.Help), $"{mode.Id} reuses another mode's help text.");
            foreach (var vendor in VendorNames)
                Assert.DoesNotContain(vendor, mode.Help, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void FiveModesCoverBothStagesInBothPlaces()
    {
        Assert.Equal(
            [
                PipelineModeId.Demo,
                PipelineModeId.CloudCloud,
                PipelineModeId.CloudLocal,
                PipelineModeId.LocalCloud,
                PipelineModeId.LocalLocal,
            ],
            PipelineMode.All.Select(m => m.Id));

        Assert.Equal(
            [
                (StageKind.Scripted, StageKind.Scripted),
                (StageKind.Cloud, StageKind.Cloud),
                (StageKind.Cloud, StageKind.Local),
                (StageKind.Local, StageKind.Cloud),
                (StageKind.Local, StageKind.Local),
            ],
            PipelineMode.All.Select(m => (m.Transcription, m.Translation)));
    }

    [Fact]
    public void ModeNamesDescribeThePipelineAndNameNoVendor()
    {
        foreach (var mode in PipelineMode.All)
        {
            foreach (var vendor in VendorNames)
                Assert.DoesNotContain(vendor, mode.Name, StringComparison.OrdinalIgnoreCase);

            if (mode.Id == PipelineModeId.Demo)
                continue;

            Assert.Contains("transcription", mode.Name, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("translation", mode.Name, StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>The one thing an operator has to be sure of before a supplier meeting.</summary>
    [Fact]
    public void EveryModeStatesWhatLeavesTheMachine()
    {
        Assert.Equal("nothing leaves this machine", PipelineMode.Of(PipelineModeId.Demo).Leaves);
        Assert.Equal("audio leaves this machine", PipelineMode.Of(PipelineModeId.CloudCloud).Leaves);
        Assert.Equal("audio leaves this machine", PipelineMode.Of(PipelineModeId.CloudLocal).Leaves);
        Assert.Equal("only text leaves this machine", PipelineMode.Of(PipelineModeId.LocalCloud).Leaves);
        Assert.Equal("nothing leaves this machine", PipelineMode.Of(PipelineModeId.LocalLocal).Leaves);
    }

    [Fact]
    public void DemoIsAlwaysAvailableAndUsesScriptedProviders()
    {
        var (downloads, _) = TempDownloads();

        var plan = PipelinePlanner.Plan(
            PipelineMode.Of(PipelineModeId.Demo), new AppSettings(), downloads, NoKey);

        Assert.Null(plan.Status.Unavailable);
        Assert.NotNull(plan.Asr);
        Assert.False(plan.Asr!.Caps.Translation); // scripted ASR routes through the MT provider
        Assert.NotNull(plan.Mt);
        Assert.Equal("Transcription: scripted", plan.Status.TranscriptionLabel);
        Assert.Equal("Translation: scripted", plan.Status.TranslationLabel);
    }

    [Fact]
    public void CloudModesAreUnavailableWithoutAKeyAndSayWhere()
    {
        var (downloads, _) = TempDownloads();

        foreach (var id in new[] { PipelineModeId.CloudCloud, PipelineModeId.CloudLocal })
        {
            var status = PipelinePlanner.Describe(
                PipelineMode.Of(id), new AppSettings(), downloads, NoKey);

            Assert.NotNull(status.Unavailable);
            Assert.Contains("key", status.Unavailable!, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("Settings", status.Unavailable!, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void CloudCloudResolvesToACloudAsrThatTranslatesItself()
    {
        var (downloads, _) = TempDownloads();

        var plan = PipelinePlanner.Plan(
            PipelineMode.Of(PipelineModeId.CloudCloud), new AppSettings(), downloads, SomeKey);

        Assert.Null(plan.Status.Unavailable);
        Assert.NotNull(plan.Asr);
        Assert.True(plan.Asr!.Caps.Translation); // the orchestrator will not call an MT provider
        Assert.Null(plan.Mt);
        Assert.True(plan.CloudTranslation);
        Assert.Equal("Translation: cloud", plan.Status.TranslationLabel);
        Assert.Contains("meeting-room", plan.Status.TranscriptionLabel);
        (plan.Asr as IDisposable)?.Dispose();
    }

    [Fact]
    public void CloudLocalNeedsADownloadedModelAndSaysWhichIsMissing()
    {
        var (downloads, _) = TempDownloads();
        var model = LocalModelCatalog.Models[0];

        var noModel = PipelinePlanner.Describe(
            PipelineMode.Of(PipelineModeId.CloudLocal), new AppSettings(), downloads, SomeKey);
        Assert.NotNull(noModel.Unavailable);
        Assert.Contains("Settings", noModel.Unavailable!, StringComparison.Ordinal);

        var notDownloaded = PipelinePlanner.Describe(
            PipelineMode.Of(PipelineModeId.CloudLocal),
            new AppSettings { ActiveTranslationModelId = model.Id }, downloads, SomeKey);
        Assert.NotNull(notDownloaded.Unavailable);
        Assert.Contains("not downloaded", notDownloaded.Unavailable!, StringComparison.OrdinalIgnoreCase);
        Assert.Equal($"Translation: {model.DisplayName} — not downloaded", notDownloaded.TranslationLabel);
    }

    /// <summary>
    /// The mechanism the capability model depends on: choosing local translation turns the
    /// cloud provider's own translation off, which flips Caps.Translation, which is the
    /// orchestrator's only decision. The mode must not become a switch inside the session.
    /// </summary>
    [Fact]
    public void CloudLocalTurnsCloudTranslationOffAndSuppliesALocalProvider()
    {
        var model = LocalModelCatalog.Models[0];
        var dir = DownloadedModelsDir(model);
        var downloads = new ModelDownloadManager(dir);

        var plan = PipelinePlanner.Plan(
            PipelineMode.Of(PipelineModeId.CloudLocal),
            new AppSettings { ActiveTranslationModelId = model.Id }, downloads, SomeKey);

        Assert.Null(plan.Status.Unavailable);
        Assert.False(plan.CloudTranslation);
        Assert.NotNull(plan.Asr);
        Assert.False(plan.Asr!.Caps.Translation);
        Assert.IsType<LlamaSharpMtProvider>(plan.Mt);
        Assert.Equal($"Translation: {model.DisplayName} (local)", plan.Status.TranslationLabel);

        (plan.Mt as IDisposable)?.Dispose();
        (plan.Asr as IDisposable)?.Dispose();
        Directory.Delete(dir, recursive: true);
    }

    [Fact]
    public void LocalTranscriptionModesAreUnavailableAndSaySoPlainly()
    {
        var model = LocalModelCatalog.Models[0];
        var dir = DownloadedModelsDir(model);
        var downloads = new ModelDownloadManager(dir);
        var settings = new AppSettings { ActiveTranslationModelId = model.Id };

        foreach (var id in new[] { PipelineModeId.LocalCloud, PipelineModeId.LocalLocal })
        {
            var status = PipelinePlanner.Describe(PipelineMode.Of(id), settings, downloads, SomeKey);

            Assert.NotNull(status.Unavailable);
            Assert.Contains("local transcription", status.Unavailable!, StringComparison.OrdinalIgnoreCase);

            var plan = PipelinePlanner.Plan(PipelineMode.Of(id), settings, downloads, SomeKey);
            Assert.Null(plan.Asr); // nothing is constructed for a mode that cannot run
            Assert.Null(plan.Mt);
        }

        Directory.Delete(dir, recursive: true);
    }

    /// <summary>
    /// local · cloud is blocked twice over: there is no local ASR, and cloud translation
    /// today exists only inside the cloud ASR session — there is no standalone text MT
    /// provider to pair with a local transcriber. Both reasons are stated.
    /// </summary>
    [Fact]
    public void LocalCloudAlsoStatesTheMissingStandaloneCloudTranslator()
    {
        var (downloads, _) = TempDownloads();

        var status = PipelinePlanner.Describe(
            PipelineMode.Of(PipelineModeId.LocalCloud), new AppSettings(), downloads, SomeKey);

        Assert.NotNull(status.Unavailable);
        Assert.Contains("local transcription", status.Unavailable!, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("cloud translation", status.Unavailable!, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Demo keeps #7's behaviour: a downloaded local model translates the scripted transcript
    /// (the only way to rehearse a model without a key), and a model that was selected but never
    /// downloaded falls back to scripted translations *loudly* rather than looking live.
    /// </summary>
    [Fact]
    public void DemoRunsASelectedLocalModelAndAnnouncesTheFallbackWhenItCannot()
    {
        var model = LocalModelCatalog.Models[0];
        var dir = DownloadedModelsDir(model);
        var settings = new AppSettings { ActiveTranslationModelId = model.Id };

        var live = PipelinePlanner.Plan(
            PipelineMode.Of(PipelineModeId.Demo), settings, new ModelDownloadManager(dir), NoKey);
        Assert.Null(live.Status.Unavailable);
        Assert.IsType<LlamaSharpMtProvider>(live.Mt);
        Assert.Equal($"Translation: {model.DisplayName} (local)", live.Status.TranslationLabel);
        (live.Mt as IDisposable)?.Dispose();

        var (empty, _) = TempDownloads();
        var fallback = PipelinePlanner.Plan(
            PipelineMode.Of(PipelineModeId.Demo), settings, empty, NoKey);
        Assert.Null(fallback.Status.Unavailable);
        Assert.NotNull(fallback.Mt); // scripted stand-in, so demo always runs
        Assert.NotNull(fallback.Substitution);
        Assert.Contains("not downloaded", fallback.Substitution!, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("scripted", fallback.Substitution!, StringComparison.OrdinalIgnoreCase);

        Directory.Delete(dir, recursive: true);
    }

    [Fact]
    public void UnknownModelIdIsNamedRatherThanSilentlyIgnored()
    {
        var (downloads, _) = TempDownloads();
        var settings = new AppSettings { ActiveTranslationModelId = "gone-model" };

        var status = PipelinePlanner.Describe(
            PipelineMode.Of(PipelineModeId.CloudLocal), settings, downloads, SomeKey);

        Assert.Equal("Translation: unknown model \"gone-model\"", status.TranslationLabel);
        Assert.NotNull(status.Unavailable);
    }

    /// <summary>Describe must construct nothing: the main window calls it on every settings
    /// change, and building the local provider is a step towards loading gigabytes of weights.</summary>
    [Fact]
    public void PlanCarriesTheSameLabelsAsDescribe()
    {
        var (downloads, _) = TempDownloads();
        var settings = new AppSettings();

        foreach (var mode in PipelineMode.All)
        {
            var described = PipelinePlanner.Describe(mode, settings, downloads, SomeKey);
            var plan = PipelinePlanner.Plan(mode, settings, downloads, SomeKey);

            Assert.Equal(described.TranscriptionLabel, plan.Status.TranscriptionLabel);
            Assert.Equal(described.TranslationLabel, plan.Status.TranslationLabel);
            Assert.Equal(described.Unavailable, plan.Status.Unavailable);

            (plan.Mt as IDisposable)?.Dispose();
            (plan.Asr as IDisposable)?.Dispose();
        }
    }

    /// <summary>Both stage labels are always set, for every mode and every failure shape —
    /// the masthead has no other way to say what is about to run.</summary>
    [Fact]
    public void BothStageLabelsAreAlwaysNamed()
    {
        var (downloads, _) = TempDownloads();
        AppSettings[] cases =
        [
            new(),
            new() { ActiveTranslationModelId = LocalModelCatalog.Models[0].Id },
            new() { ActiveTranslationModelId = "gone-model" },
        ];

        foreach (var mode in PipelineMode.All)
        foreach (var settings in cases)
        foreach (var key in new[] { NoKey, SomeKey })
        {
            var status = PipelinePlanner.Describe(mode, settings, downloads, key);
            Assert.StartsWith("Transcription: ", status.TranscriptionLabel, StringComparison.Ordinal);
            Assert.StartsWith("Translation: ", status.TranslationLabel, StringComparison.Ordinal);
            foreach (var vendor in VendorNames.Where(v => v != "Qwen" && v != "Gemma"))
            {
                Assert.DoesNotContain(vendor, status.TranscriptionLabel, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain(vendor, status.TranslationLabel, StringComparison.OrdinalIgnoreCase);
            }
        }
    }
}
