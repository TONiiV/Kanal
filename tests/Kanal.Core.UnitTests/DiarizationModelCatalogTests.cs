using Kanal.Core.Models;

namespace Kanal.Core.UnitTests;

/// <summary>
/// Sizes and hashes here were read from the GitHub releases API and recomputed from the
/// downloaded files on 2026-09-08. A catalog entry nobody can verify is worse than no entry.
/// </summary>
public class DiarizationModelCatalogTests
{
    [Fact]
    public void OneSegmentationModelAndFourEmbeddingCandidates()
    {
        Assert.Equal("pyannote-segmentation-3-0", Assert.Single(DiarizationModelCatalog.Segmentation).Id);
        Assert.Equal(4, DiarizationModelCatalog.Embeddings.Count);
        Assert.Equal(
            DiarizationModelCatalog.Segmentation.Count + DiarizationModelCatalog.Embeddings.Count,
            DiarizationModelCatalog.Models.Count);
    }

    [Fact]
    public void EveryEntryDeclaresItsRoleFileSizeHashAndLicence()
    {
        foreach (var m in DiarizationModelCatalog.Models)
        {
            Assert.False(string.IsNullOrWhiteSpace(m.Id));
            Assert.False(string.IsNullOrWhiteSpace(m.DisplayName));
            Assert.Matches("^[0-9a-f]{64}$", m.Sha256);
            Assert.InRange(m.SizeBytes, 1_000_000, 100_000_000);
            Assert.False(string.IsNullOrWhiteSpace(m.License.Name));
            Assert.True(m.License.PermitsCommercialUse);
        }

        Assert.All(DiarizationModelCatalog.Segmentation,
            m => Assert.Equal(DiarizationModelRole.Segmentation, m.Role));
        Assert.All(DiarizationModelCatalog.Embeddings,
            m => Assert.Equal(DiarizationModelRole.Embedding, m.Role));
    }

    /// <summary>
    /// Upstream spelled the embedding release tag <c>speaker-recongition-models</c>. Corrected
    /// to "recognition" the URL is a 404, and the failure arrives as a download error rather
    /// than as a compile error.
    /// </summary>
    [Fact]
    public void EmbeddingUrlsKeepUpstreamsMisspeltReleaseTag()
    {
        foreach (var m in DiarizationModelCatalog.Embeddings)
        {
            Assert.Equal(
                $"https://github.com/k2-fsa/sherpa-onnx/releases/download/speaker-recongition-models/{m.FileName}",
                m.DownloadUrl);
        }
    }

    /// <summary>The whole point of the URL change: these assets are on GitHub releases, need no
    /// token, and are not a HuggingFace repo path the downloader can compose.</summary>
    [Fact]
    public void EveryDownloadUrlIsAWholeGitHubReleaseUrl()
    {
        foreach (var m in DiarizationModelCatalog.Models)
        {
            Assert.StartsWith("https://github.com/k2-fsa/sherpa-onnx/releases/download/", m.DownloadUrl);
            Assert.EndsWith(m.FileName, m.DownloadUrl);
            Assert.DoesNotContain("huggingface", m.DownloadUrl);
        }
    }

    [Fact]
    public void SegmentationIsThePyannoteArchiveVerifiedAgainstTheRelease()
    {
        var m = DiarizationModelCatalog.Segmentation[0];

        Assert.Equal("sherpa-onnx-pyannote-segmentation-3-0.tar.bz2", m.FileName);
        Assert.Equal(6_958_444, m.SizeBytes);
        Assert.Equal("24615ee884c897d9d2ba09bb4d30da6bb1b15e685065962db5b02e76e4996488", m.Sha256);
        Assert.Equal("MIT", m.License.Name);
        Assert.Equal(
            "https://github.com/k2-fsa/sherpa-onnx/releases/download/speaker-segmentation-models/sherpa-onnx-pyannote-segmentation-3-0.tar.bz2",
            m.DownloadUrl);
    }

    [Theory]
    [InlineData("3dspeaker-campplus-zh-en", "3dspeaker_speech_campplus_sv_zh_en_16k-common_advanced.onnx",
        28_281_164, "aa3cfc16963a10586a9393f5035d6d6b57e98d358b347f80c2a30bf4f00ceba2", "Apache-2.0")]
    [InlineData("3dspeaker-eres2net-base-zh", "3dspeaker_speech_eres2net_base_sv_zh-cn_3dspeaker_16k.onnx",
        39_593_761, "1a331345f04805badbb495c775a6ddffcdd1a732567d5ec8b3d5749e3c7a5e4b", "Apache-2.0")]
    [InlineData("wespeaker-cnceleb-resnet34", "wespeaker_zh_cnceleb_resnet34_LM.onnx",
        26_530_548, "87d1d5068397f3792c730570b53d66cd8be1da7ea22dd04f5b6706d96a3cd168", "Apache-2.0")]
    [InlineData("titanet-small", "nemo_en_titanet_small.onnx",
        40_257_283, "ad4a1802485d8b34c722d2a9d04249662f2ece5d28a7a039063ca22f515a789e", "CC-BY-4.0")]
    public void EmbeddingCandidatesCarryTheirVerifiedSizeHashAndLicence(
        string id, string fileName, long size, string sha256, string license)
    {
        var m = DiarizationModelCatalog.Find(id);

        Assert.NotNull(m);
        Assert.Equal(fileName, m.FileName);
        Assert.Equal(size, m.SizeBytes);
        Assert.Equal(sha256, m.Sha256);
        Assert.Equal(license, m.License.Name);
    }

    /// <summary>CC-BY-4.0 is usable, but only if whoever ships it is told what it asks for.</summary>
    [Fact]
    public void AttributionLicencesSayWhatTheyRequire()
    {
        foreach (var m in DiarizationModelCatalog.Models.Where(m => m.License.Name.StartsWith("CC-BY")))
            Assert.False(string.IsNullOrWhiteSpace(m.LicenseNote), $"{m.Id} states no attribution");

        foreach (var m in DiarizationModelCatalog.Models.Where(m => m.License.Name is "MIT" or "Apache-2.0"))
            Assert.Null(m.LicenseNote);
    }

    /// <summary>
    /// Rev's Reverb models sit in the same GitHub release as the segmentation model above, one
    /// line away in the asset list, under a licence whose §3.2 forbids commercial use; DiariZen's
    /// weights are CC-BY-NC-4.0. Adding either has to fail at construction — a reviewer noticing
    /// is not a mechanism.
    /// </summary>
    [Theory]
    [MemberData(nameof(NonCommercialLicences))]
    public void ANonCommercialModelCannotBeAddedToACatalogAtAll(ModelLicense license)
    {
        var ex = Assert.Throws<ArgumentException>(() => new DiarizationModelInfo(
            Id: "reverb-diarization-v1",
            DisplayName: "Reverb diarization v1",
            Role: DiarizationModelRole.Segmentation,
            ReleaseTag: "speaker-segmentation-models",
            FileName: "sherpa-onnx-reverb-diarization-v1.tar.bz2",
            SizeBytes: 10_918_585,
            Sha256: new string('0', 64),
            License: license));

        Assert.Contains("commercial", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    public static TheoryData<ModelLicense> NonCommercialLicences =>
        [ModelLicense.RevNonProduction, ModelLicense.CcByNc40];

    [Fact]
    public void TheExcludedModelsAreNotInTheCatalog()
    {
        foreach (var banned in new[] { "reverb", "diarizen" })
            Assert.DoesNotContain(DiarizationModelCatalog.Models,
                m => m.FileName.Contains(banned, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void FindResolvesByIdAndToleratesUnknown()
    {
        Assert.NotNull(DiarizationModelCatalog.Find("pyannote-segmentation-3-0"));
        Assert.Null(DiarizationModelCatalog.Find("reverb-diarization-v2"));
        Assert.Null(DiarizationModelCatalog.Find(null));
    }

    [Fact]
    public void SizeLabelIsHumanReadable()
    {
        Assert.Equal("6.6 MB", DiarizationModelCatalog.Segmentation[0].SizeLabel);
        Assert.Equal("38.4 MB", DiarizationModelCatalog.Find("titanet-small")!.SizeLabel);
    }
}
