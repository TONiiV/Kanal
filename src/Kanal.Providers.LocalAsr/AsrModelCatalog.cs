using Kanal.Core.Models;

namespace Kanal.Providers.LocalAsr;

// FileName is the local name and carries the model id: every chunk configuration publishes a file
// called encoder.int8.onnx, and they all land in one models directory.
public sealed record AsrModelFile(
    string FileName,
    string Repo,
    string RemoteName,
    long SizeBytes,
    string Sha256) : IDownloadableFile
{
    public string DownloadUrl => HuggingFace.FileUrl(Repo, RemoteName);
}

/// <summary>One downloadable transcription model. All values verified against the HF API 2026-09-08.</summary>
public sealed record AsrModelInfo(
    string Id,
    string DisplayName,
    string Parameters,
    string Repo,
    IReadOnlyList<AsrModelFile> Parts,
    string License,
    string? LicenseNote = null)
{
    public long SizeBytes => Parts.Sum(p => p.SizeBytes);

    public string SizeLabel => $"{SizeBytes / (1024.0 * 1024 * 1024):0.0} GB";
}

/// <summary>
/// Nemotron 3.5 ASR Streaming 0.6B in sherpa-onnx transducer packaging, at three of its five
/// published chunk sizes — see <c>docs/adr/0053-local-transcription-model-and-runtime.md</c>.
/// Order matters: the first entry is the recommended default.
/// </summary>
public static class AsrModelCatalog
{
    private const string License = "OpenMDW-1.1";

    private const string LicenseNote =
        "OpenMDW-1.1 — permissive but not OSI-approved; review before redistribution.";

    // Not an LFS object, so this hash was computed from the downloaded file rather than read
    // off the API the way every other hash here was.
    private const string TokensSha = "729cc103155bafa785f9cd45746cd41cabe97eab7182fc04d594129587958f8a";

    private const long TokensBytes = 131_440;

    private const long DecoderBytes = 14_978_075;

    private const string DecoderSha = "19f9c98fc6d0a2c33a65a43b36fdb2e914c26c0aa9764be3aebc502a1e982fb0";

    private const long JoinerBytes = 9_504_438;

    private const string JoinerSha = "4101c7c679a0bc30483794b27a059e34e79232aa2068d78d51231a22c8b0d7ce";

    public static IReadOnlyList<AsrModelInfo> Models { get; } =
    [
        Package("560", "0.6B · int8 · 560 ms", 657_601_403,
            "012e9321373af99021415e0b0eb3ec827b4be3153be6f30d9b448fe65e896e68"),
        Package("160", "0.6B · int8 · 160 ms", 657_601_518,
            "e1b39e5e16bef578a54ed2fba5f031438e000cc36c3ea2ca49d55699d5baebd4"),
        Package("1120", "0.6B · int8 · 1120 ms", 657_601_521,
            "2fff2166acaa535bd969fb223c1f0783d71029f143cb298bc54c2afe85abf772"),
    ];

    public static AsrModelInfo? Find(string? id) =>
        id is null ? null : Models.FirstOrDefault(m => m.Id == id);

    private static AsrModelInfo Package(string chunkMs, string parameters, long encoderBytes, string encoderSha)
    {
        var id = $"nemotron-3.5-asr-{chunkMs}ms-int8";
        var repo = $"csukuangfj2/sherpa-onnx-nemotron-3.5-asr-streaming-0.6b-{chunkMs}ms-int8-2026-06-11";

        return new AsrModelInfo(
            Id: id,
            DisplayName: $"Nemotron 3.5 ASR Streaming ({chunkMs} ms)",
            Parameters: parameters,
            Repo: repo,
            Parts:
            [
                new($"{id}-encoder.int8.onnx", repo, "encoder.int8.onnx", encoderBytes, encoderSha),
                new($"{id}-decoder.int8.onnx", repo, "decoder.int8.onnx", DecoderBytes, DecoderSha),
                new($"{id}-joiner.int8.onnx", repo, "joiner.int8.onnx", JoinerBytes, JoinerSha),
                new($"{id}-tokens.txt", repo, "tokens.txt", TokensBytes, TokensSha),
            ],
            License: License,
            LicenseNote: LicenseNote);
    }
}
