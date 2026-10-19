using Kanal.Providers.LocalMt;

namespace Kanal.Core.UnitTests;

public class LocalModelCatalogTests
{
    [Fact]
    public void PreferredModelIsHyMt2()
    {
        var first = LocalModelCatalog.Models[0];
        Assert.Equal("hy-mt2-1.8b", first.Id);
        Assert.Equal("Hy-MT2 1.8B", first.DisplayName);
        Assert.Equal("1.8B · Q4_K_M", first.Parameters);
        Assert.Equal("tencent/Hy-MT2-1.8B-GGUF", first.Repo);
        Assert.Equal("Hy-MT2-1.8B-Q4_K_M.gguf", first.FileName);
        Assert.Equal(1_133_080_448, first.SizeBytes);
        Assert.Equal("dc5f44fcf1fa496ee7ad725982c0c8c553a4de00259b53af84c4b89fb0c06699", first.Sha256);
        Assert.Equal("Apache-2.0", first.License);
        Assert.Null(first.LicenseNote); // Apache needs no warning
    }

    [Fact]
    public void HyMt2CarriesNoPrefill()
    {
        Assert.Null(LocalModelCatalog.Find("hy-mt2-1.8b")!.AssistantPrefill);
    }

    [Fact]
    public void PreviousModelsStayInTheCatalog()
    {
        foreach (var id in new[] { "qwen3.5-4b", "qwen3.5-2b", "gemma-3-4b" })
            Assert.NotNull(LocalModelCatalog.Find(id));
    }

    /// <summary>
    /// A reasoning model with a translation-sized token budget spends the whole budget on
    /// <c>&lt;think&gt;</c> and emits no translation at all — measured on the 2B here: 40 s per
    /// call and an empty string out of <see cref="MtOutputCleaner"/>, which is the correct
    /// reading of an unterminated think block. Prefilling a closed one turns that into 1 s and
    /// an actual sentence. Any Qwen3.x added to this catalog needs the same prefill, so the
    /// requirement is asserted on the family rather than on the two entries that exist today.
    /// </summary>
    [Fact]
    public void ReasoningModelsSuppressTheirThinkingTurn()
    {
        var reasoning = LocalModelCatalog.Models.Where(m => m.Id.StartsWith("qwen3")).ToList();
        Assert.NotEmpty(reasoning);
        foreach (var m in reasoning)
            Assert.Equal("<think>\n\n</think>\n\n", m.AssistantPrefill);
    }

    /// <summary>The prefill is a fix for reasoning models, not a thing every model wants:
    /// injected into one that does not reason, it is literal text in the translation.</summary>
    [Fact]
    public void NonReasoningModelsCarryNoPrefill()
    {
        Assert.Null(LocalModelCatalog.Find("gemma-3-4b")!.AssistantPrefill);
    }

    [Fact]
    public void EveryEntryIsComplete()
    {
        Assert.InRange(LocalModelCatalog.Models.Count, 4, 6);
        foreach (var m in LocalModelCatalog.Models)
        {
            Assert.False(string.IsNullOrWhiteSpace(m.Id));
            Assert.False(string.IsNullOrWhiteSpace(m.DisplayName));
            Assert.False(string.IsNullOrWhiteSpace(m.Parameters));
            Assert.Matches("^[0-9a-f]{64}$", m.Sha256);
            Assert.True(m.SizeBytes > 500_000_000, $"{m.Id} size looks wrong");
            Assert.EndsWith(".gguf", m.FileName);
            Assert.StartsWith("https://huggingface.co/", m.DownloadUrl);
            Assert.EndsWith(m.FileName, m.DownloadUrl);
            Assert.False(string.IsNullOrWhiteSpace(m.License));
        }
    }

    [Fact]
    public void NonPermissiveLicensesCarryANote()
    {
        foreach (var m in LocalModelCatalog.Models)
        {
            var permissive = m.License is "Apache-2.0" or "MIT";
            if (!permissive)
                Assert.False(string.IsNullOrWhiteSpace(m.LicenseNote),
                    $"{m.Id} has license {m.License} and must carry a LicenseNote");
        }
    }

    [Fact]
    public void FindResolvesByIdAndToleratesUnknown()
    {
        Assert.NotNull(LocalModelCatalog.Find("qwen3.5-4b"));
        Assert.NotNull(LocalModelCatalog.Find("hy-mt2-1.8b"));
        Assert.Null(LocalModelCatalog.Find("no-such-model"));
        Assert.Null(LocalModelCatalog.Find(null));
    }

    [Fact]
    public void SizeLabelIsHumanReadable()
    {
        var m = LocalModelCatalog.Models[0];
        Assert.Contains("GB", m.SizeLabel);
    }
}
