using Kanal.Providers.LocalAsr;

namespace Kanal.Core.UnitTests;

public class AsrModelCatalogTests
{
    [Fact]
    public void PreferredModelIsTheFiveHundredAndSixtyMillisecondNemotron()
    {
        var first = AsrModelCatalog.Models[0];
        Assert.Equal("nemotron-3.5-asr-560ms-int8", first.Id);
    }

    [Fact]
    public void EveryModelCarriesItsFourParts()
    {
        foreach (var m in AsrModelCatalog.Models)
        {
            Assert.Equal(4, m.Parts.Count);
            Assert.Contains(m.Parts, p => p.DownloadUrl.EndsWith("/encoder.int8.onnx"));
            Assert.Contains(m.Parts, p => p.DownloadUrl.EndsWith("/decoder.int8.onnx"));
            Assert.Contains(m.Parts, p => p.DownloadUrl.EndsWith("/joiner.int8.onnx"));
            Assert.Contains(m.Parts, p => p.DownloadUrl.EndsWith("/tokens.txt"));
        }
    }

    [Fact]
    public void EveryEntryIsComplete()
    {
        Assert.InRange(AsrModelCatalog.Models.Count, 2, 5);
        foreach (var m in AsrModelCatalog.Models)
        {
            Assert.False(string.IsNullOrWhiteSpace(m.Id));
            Assert.False(string.IsNullOrWhiteSpace(m.DisplayName));
            Assert.False(string.IsNullOrWhiteSpace(m.Parameters));
            Assert.False(string.IsNullOrWhiteSpace(m.License));
            foreach (var p in m.Parts)
            {
                Assert.Matches("^[0-9a-f]{64}$", p.Sha256);
                Assert.True(p.SizeBytes > 0, $"{m.Id}/{p.FileName} size looks wrong");
                Assert.StartsWith($"https://huggingface.co/{m.Repo}/resolve/main/", p.DownloadUrl);
            }
            Assert.True(m.SizeBytes > 500_000_000, $"{m.Id} total size looks wrong");
            Assert.Contains("GB", m.SizeLabel);
        }
    }

    [Fact]
    public void NonPermissiveLicensesCarryANote()
    {
        foreach (var m in AsrModelCatalog.Models)
        {
            var permissive = m.License is "Apache-2.0" or "MIT";
            if (!permissive)
                Assert.False(string.IsNullOrWhiteSpace(m.LicenseNote),
                    $"{m.Id} has license {m.License} and must carry a LicenseNote");
        }
    }

    /// <summary>Downloading a second chunk configuration would otherwise overwrite the first
    /// while both still claim to be on disk.</summary>
    [Fact]
    public void LocalFileNamesAreUniqueAcrossTheCatalog()
    {
        var names = AsrModelCatalog.Models.SelectMany(m => m.Parts).Select(p => p.FileName).ToList();
        Assert.Equal(names.Count, names.Distinct().Count());
        foreach (var m in AsrModelCatalog.Models)
            Assert.All(m.Parts, p => Assert.StartsWith(m.Id, p.FileName));
    }

    [Fact]
    public void FindResolvesByIdAndToleratesUnknown()
    {
        Assert.NotNull(AsrModelCatalog.Find("nemotron-3.5-asr-560ms-int8"));
        Assert.Null(AsrModelCatalog.Find("no-such-model"));
        Assert.Null(AsrModelCatalog.Find(null));
    }
}
