namespace Kanal.Providers.LocalMt;

/// <summary>One downloadable translation model. All values verified against the HF API 2026-07-31.</summary>
public sealed record LocalModelInfo(
    string Id,
    string DisplayName,
    string Parameters,
    string Repo,
    string FileName,
    long SizeBytes,
    string Sha256,
    string License,
    string? LicenseNote = null)
{
    public string DownloadUrl => $"https://huggingface.co/{Repo}/resolve/main/{FileName}";

    public string SizeLabel => $"{SizeBytes / (1024.0 * 1024 * 1024):0.0} GB";
}

/// <summary>
/// Curated catalog of small instruct models that survived the zh/de/pl A/B bench
/// (part numbers preserved, no source-script leakage, correct terminology).
/// Order matters: the first entry is the recommended default.
/// </summary>
public static class LocalModelCatalog
{
    public static IReadOnlyList<LocalModelInfo> Models { get; } =
    [
        new(
            Id: "qwen3.5-4b",
            DisplayName: "Qwen3.5 4B",
            Parameters: "4B · Q4_K_M",
            Repo: "unsloth/Qwen3.5-4B-GGUF",
            FileName: "Qwen3.5-4B-Q4_K_M.gguf",
            SizeBytes: 2_740_937_888,
            Sha256: "00fe7986ff5f6b463e62455821146049db6f9313603938a70800d1fb69ef11a4",
            License: "Apache-2.0"),
        new(
            Id: "qwen3.5-2b",
            DisplayName: "Qwen3.5 2B",
            Parameters: "2B · Q4_K_M",
            Repo: "unsloth/Qwen3.5-2B-GGUF",
            FileName: "Qwen3.5-2B-Q4_K_M.gguf",
            SizeBytes: 1_280_835_840,
            Sha256: "aaf42c8b7c3cab2bf3d69c355048d4a0ee9973d48f16c731c0520ee914699223",
            License: "Apache-2.0"),
        new(
            Id: "gemma-3-4b",
            DisplayName: "Gemma 3 4B",
            Parameters: "4B · Q4_K_M",
            Repo: "ggml-org/gemma-3-4b-it-GGUF",
            FileName: "gemma-3-4b-it-Q4_K_M.gguf",
            SizeBytes: 2_489_757_856,
            Sha256: "882e8d2db44dc554fb0ea5077cb7e4bc49e7342a1f0da57901c0802ea21a0863",
            License: "Gemma",
            LicenseNote: "Gemma Terms of Use — not OSI-approved; review before redistribution."),
    ];

    public static LocalModelInfo? Find(string? id) =>
        id is null ? null : Models.FirstOrDefault(m => m.Id == id);
}
