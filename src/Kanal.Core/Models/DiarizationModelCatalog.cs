namespace Kanal.Core.Models;

public enum DiarizationModelRole
{
    Segmentation,
    Embedding,
}

public enum DiarizationReadiness
{
    NotDownloaded,
    Downloading,
    Ready,
    Failed,
}

public sealed record ModelLicense
{
    public static readonly ModelLicense Mit = Commercial("MIT");

    public static readonly ModelLicense Apache20 = Commercial("Apache-2.0");

    public static readonly ModelLicense CcBy40 = Commercial(
        "CC-BY-4.0",
        "Attribution required: NVIDIA NeMo TitaNet, CC BY 4.0 — credit it wherever this build is shipped.");

    /// <summary>Nameable so a model under it can be rejected, never so one can be listed.</summary>
    public static readonly ModelLicense RevNonProduction =
        new("Rev Model Non-Production License", permitsCommercialUse: false, note: null);

    public static readonly ModelLicense CcByNc40 =
        new("CC-BY-NC-4.0", permitsCommercialUse: false, note: null);

    private ModelLicense(string name, bool permitsCommercialUse, string? note)
    {
        Name = name;
        PermitsCommercialUse = permitsCommercialUse;
        Note = note;
    }

    private static ModelLicense Commercial(string name, string? note = null) => new(name, true, note);

    public string Name { get; }

    public bool PermitsCommercialUse { get; }

    public string? Note { get; }
}

public sealed record DiarizationModelInfo(
    string Id,
    string DisplayName,
    DiarizationModelRole Role,
    string ReleaseTag,
    string FileName,
    long SizeBytes,
    string Sha256,
    ModelLicense License) : IDownloadableFile
{
    private readonly ModelLicense _license = Permitted(License);

    public ModelLicense License
    {
        get => _license;
        init => _license = Permitted(value);
    }

    public string DownloadUrl =>
        $"https://github.com/k2-fsa/sherpa-onnx/releases/download/{ReleaseTag}/{FileName}";

    public string SizeLabel => $"{SizeBytes / (1024.0 * 1024):0.#} MB";

    public string? LicenseNote => License.Note;

    // sherpa-onnx ships Rev's non-commercial Reverb models in the same release as the segmentation
    // model below; a licence that forbids commercial use has to fail here, not at review.
    private static ModelLicense Permitted(ModelLicense license) =>
        license.PermitsCommercialUse
            ? license
            : throw new ArgumentException(
                $"{license.Name} forbids commercial use; a model under it cannot enter the catalog.",
                nameof(license));
}

/// <summary>Sizes and SHA-256 read from the GitHub releases API and recomputed from the
/// downloaded files on 2026-09-08.</summary>
public static class DiarizationModelCatalog
{
    private const string SegmentationTag = "speaker-segmentation-models";

    // Upstream misspelt "recognition". Corrected, every embedding URL is a 404.
    private const string EmbeddingTag = "speaker-recongition-models";

    // The published asset is a bz2 archive of model.onnx and model.int8.onnx; the BCL has no
    // bzip2, so unpacking belongs to the slice that loads the model.
    public static IReadOnlyList<DiarizationModelInfo> Segmentation { get; } =
    [
        new(
            Id: "pyannote-segmentation-3-0",
            DisplayName: "pyannote segmentation 3.0",
            Role: DiarizationModelRole.Segmentation,
            ReleaseTag: SegmentationTag,
            FileName: "sherpa-onnx-pyannote-segmentation-3-0.tar.bz2",
            SizeBytes: 6_958_444,
            Sha256: "24615ee884c897d9d2ba09bb4d30da6bb1b15e685065962db5b02e76e4996488",
            License: ModelLicense.Mit),
    ];

    // Candidates rather than a ranking: the default is decided by measured DER on zh/de/pl.
    public static IReadOnlyList<DiarizationModelInfo> Embeddings { get; } =
    [
        new(
            Id: "3dspeaker-campplus-zh-en",
            DisplayName: "3D-Speaker CAM++ zh/en advanced",
            Role: DiarizationModelRole.Embedding,
            ReleaseTag: EmbeddingTag,
            FileName: "3dspeaker_speech_campplus_sv_zh_en_16k-common_advanced.onnx",
            SizeBytes: 28_281_164,
            Sha256: "aa3cfc16963a10586a9393f5035d6d6b57e98d358b347f80c2a30bf4f00ceba2",
            License: ModelLicense.Apache20),
        new(
            Id: "3dspeaker-eres2net-base-zh",
            DisplayName: "3D-Speaker ERes2Net base zh-cn",
            Role: DiarizationModelRole.Embedding,
            ReleaseTag: EmbeddingTag,
            FileName: "3dspeaker_speech_eres2net_base_sv_zh-cn_3dspeaker_16k.onnx",
            SizeBytes: 39_593_761,
            Sha256: "1a331345f04805badbb495c775a6ddffcdd1a732567d5ec8b3d5749e3c7a5e4b",
            License: ModelLicense.Apache20),
        new(
            Id: "wespeaker-cnceleb-resnet34",
            DisplayName: "WeSpeaker CN-Celeb ResNet34 LM",
            Role: DiarizationModelRole.Embedding,
            ReleaseTag: EmbeddingTag,
            FileName: "wespeaker_zh_cnceleb_resnet34_LM.onnx",
            SizeBytes: 26_530_548,
            Sha256: "87d1d5068397f3792c730570b53d66cd8be1da7ea22dd04f5b6706d96a3cd168",
            License: ModelLicense.Apache20),
        new(
            Id: "titanet-small",
            DisplayName: "NeMo TitaNet small",
            Role: DiarizationModelRole.Embedding,
            ReleaseTag: EmbeddingTag,
            FileName: "nemo_en_titanet_small.onnx",
            SizeBytes: 40_257_283,
            Sha256: "ad4a1802485d8b34c722d2a9d04249662f2ece5d28a7a039063ca22f515a789e",
            License: ModelLicense.CcBy40),
    ];

    public static IReadOnlyList<DiarizationModelInfo> Models { get; } = [.. Segmentation, .. Embeddings];

    public static DiarizationModelInfo? Find(string? id) =>
        id is null ? null : Models.FirstOrDefault(m => m.Id == id);

    /// <summary>What is on disk. An unset, unknown or mismatched pair reads as not downloaded
    /// rather than as an error — a meeting starts without speaker attribution.</summary>
    public static DiarizationReadiness Readiness(
        ModelDownloadManager downloads, string? segmentationId, string? embeddingId)
    {
        var segmentation = Find(segmentationId);
        var embedding = Find(embeddingId);
        if (segmentation is not { Role: DiarizationModelRole.Segmentation } ||
            embedding is not { Role: DiarizationModelRole.Embedding })
            return DiarizationReadiness.NotDownloaded;

        return downloads.IsDownloaded(segmentation) && downloads.IsDownloaded(embedding)
            ? DiarizationReadiness.Ready
            : DiarizationReadiness.NotDownloaded;
    }
}
