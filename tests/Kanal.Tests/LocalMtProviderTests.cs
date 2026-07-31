using Kanal.Core.Models;
using Kanal.Providers.LocalMt;

namespace Kanal.Tests;

public class MtPromptTests
{
    [Theory]
    [InlineData("de", "German")]
    [InlineData("pl", "Polish")]
    [InlineData("zh", "Chinese")]
    [InlineData("en", "English")]
    public void UsesEnglishLanguageName(string code, string name)
    {
        var prompt = MtPrompt.Build("这批支架的料号是 KX-4402。", code);
        Assert.Contains($"Translate the following into {name}.", prompt);
    }

    [Fact]
    public void ContainsInstructionAndSourceText()
    {
        var prompt = MtPrompt.Build("Wir brauchen das Erstmusterprüfprotokoll für KX-4402.", "pl");
        Assert.Contains("Output ONLY the translation, no explanations.", prompt);
        Assert.Contains("Keep part numbers, standards and units exactly as written.", prompt);
        Assert.EndsWith("Wir brauchen das Erstmusterprüfprotokoll für KX-4402.", prompt);
    }

    [Fact]
    public void UnknownCodeFallsBackToCodeItself()
    {
        Assert.Contains("Translate the following into xx.", MtPrompt.Build("t", "xx"));
    }
}

public class MtOutputCleanerTests
{
    [Fact]
    public void PassesPlainTranslationThrough()
    {
        Assert.Equal(
            "Numer katalogowy tych wsporników to KX-4402.",
            MtOutputCleaner.Clean("Numer katalogowy tych wsporników to KX-4402.\n"));
    }

    [Fact]
    public void StripsThinkBlocks()
    {
        var raw = "<think>\nThe user wants Polish. 支架 means bracket.\n</think>\n\nCzy próbki będą zgodne z normą ISO 7599?";
        Assert.Equal("Czy próbki będą zgodne z normą ISO 7599?", MtOutputCleaner.Clean(raw));
    }

    [Fact]
    public void UnterminatedThinkBlockYieldsEmpty()
    {
        Assert.Equal("", MtOutputCleaner.Clean("<think>\nstill reasoning about brackets"));
    }

    [Theory]
    [InlineData("\"Die Halterungen sind bestätigt.\"")]
    [InlineData("„Die Halterungen sind bestätigt.“")]
    [InlineData("«Die Halterungen sind bestätigt.»")]
    [InlineData("「Die Halterungen sind bestätigt.」")]
    public void StripsWrappingQuotes(string raw)
    {
        Assert.Equal("Die Halterungen sind bestätigt.", MtOutputCleaner.Clean(raw));
    }

    [Fact]
    public void KeepsInnerQuotesIntact()
    {
        var text = "Der Standard heißt \"ISO 7599\" und bleibt gleich.";
        Assert.Equal(text, MtOutputCleaner.Clean(text));
    }

    [Theory]
    [InlineData("Translation: Musimy potwierdzić termin dostawy.")]
    [InlineData("translation： Musimy potwierdzić termin dostawy.")]
    [InlineData("Übersetzung: Musimy potwierdzić termin dostawy.")]
    [InlineData("翻译： Musimy potwierdzić termin dostawy.")]
    public void StripsTranslationLabels(string raw)
    {
        Assert.Equal("Musimy potwierdzić termin dostawy.", MtOutputCleaner.Clean(raw));
    }

    [Fact]
    public void PreservesPartNumbersExactly()
    {
        var raw = "<think>ok</think>\n\"Die Teilenummer dieser Halterungen ist KX-4402, Oberflächenbehandlung nach ISO 7599.\"";
        var cleaned = MtOutputCleaner.Clean(raw);
        Assert.Contains("KX-4402", cleaned);
        Assert.Contains("ISO 7599", cleaned);
    }

    [Fact]
    public void EmptyAndWhitespaceYieldEmpty()
    {
        Assert.Equal("", MtOutputCleaner.Clean(""));
        Assert.Equal("", MtOutputCleaner.Clean("  \n "));
    }
}

public class LlamaSharpMtProviderTests
{
    private sealed class FakeTextGenerator : ITextGenerator
    {
        public List<string> Prompts { get; } = new();
        public Func<string, string> Respond { get; set; } = p => "out";

        public Task<string> GenerateAsync(string prompt, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Prompts.Add(prompt);
            return Task.FromResult(Respond(prompt));
        }
    }

    private static readonly IReadOnlyList<Utterance> NoContext = Array.Empty<Utterance>();

    [Fact]
    public async Task TranslatesIntoEveryTargetLanguage()
    {
        var generator = new FakeTextGenerator
        {
            Respond = p => p.Contains("German") ? "Die Halterungen." : "Wsporniki.",
        };
        var provider = new LlamaSharpMtProvider(generator);

        var result = await provider.TranslateAsync(
            "这批支架。", "zh", ["de", "pl"], NoContext, CancellationToken.None);

        Assert.Equal(2, result.Count);
        Assert.Equal("Die Halterungen.", result["de"]);
        Assert.Equal("Wsporniki.", result["pl"]);
        Assert.Equal(2, generator.Prompts.Count);
    }

    [Fact]
    public async Task SkipsSourceLanguageTarget()
    {
        var generator = new FakeTextGenerator();
        var provider = new LlamaSharpMtProvider(generator);

        var result = await provider.TranslateAsync(
            "text", "zh", ["zh", "de"], NoContext, CancellationToken.None);

        Assert.Single(result);
        Assert.True(result.ContainsKey("de"));
        Assert.Single(generator.Prompts);
    }

    [Fact]
    public async Task CleansGeneratorOutput()
    {
        var generator = new FakeTextGenerator
        {
            Respond = _ => "<think>reasoning</think>\n\"Wsporniki KX-4402.\"",
        };
        var provider = new LlamaSharpMtProvider(generator);

        var result = await provider.TranslateAsync(
            "支架 KX-4402。", "zh", ["pl"], NoContext, CancellationToken.None);

        Assert.Equal("Wsporniki KX-4402.", result["pl"]);
    }

    [Fact]
    public async Task OmitsTargetWhenOutputCleansToEmpty()
    {
        var generator = new FakeTextGenerator
        {
            Respond = p => p.Contains("German") ? "<think>never stops" : "Wsporniki.",
        };
        var provider = new LlamaSharpMtProvider(generator);

        var result = await provider.TranslateAsync(
            "支架。", "zh", ["de", "pl"], NoContext, CancellationToken.None);

        Assert.Single(result);
        Assert.Equal("Wsporniki.", result["pl"]);
    }

    [Fact]
    public async Task CancellationStopsTheLoop()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var provider = new LlamaSharpMtProvider(new FakeTextGenerator());

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            provider.TranslateAsync("text", "zh", ["de"], NoContext, cts.Token));
    }

    [Fact]
    public void HasStableId()
    {
        Assert.Equal("local-llm", new LlamaSharpMtProvider(new FakeTextGenerator()).Id);
    }
}
