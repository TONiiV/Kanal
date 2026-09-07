using Kanal.Providers.LocalMt;

namespace Kanal.Core.UnitTests;

public class GeneratedMeetingTitlerTests
{
    private sealed class Generator(string reply) : ITextGenerator
    {
        public string Prompt { get; private set; } = "";

        public Task<string> GenerateAsync(string prompt, CancellationToken ct)
        {
            Prompt = prompt;
            return Task.FromResult(reply);
        }
    }

    private static async Task<string?> TitleFrom(string reply, params string[] lines)
    {
        var titler = new GeneratedMeetingTitler(new Generator(reply));
        return await titler.SuggestAsync(lines, CancellationToken.None);
    }

    [Fact]
    public async Task TheTitleIsTheModelsFirstLineWithItsScaffoldingRemoved()
    {
        Assert.Equal("Tolerance review", await TitleFrom("\"Tolerance review\"\nA meeting about...", "a"));
    }

    [Fact]
    public async Task ALabelledAnswerIsUnwrapped()
    {
        Assert.Equal("Werkzeugübergabe", await TitleFrom("Title: Werkzeugübergabe", "a"));
    }

    [Fact]
    public async Task ASentenceOfAnAnswerIsRefusedRatherThanTruncatedMidWord()
    {
        var essay = string.Join(" ", Enumerable.Repeat("wsporników", 12));
        Assert.Null(await TitleFrom(essay, "a"));
    }

    [Fact]
    public async Task AnAnswerOfNothingIsNoTitle()
    {
        Assert.Null(await TitleFrom("   ", "a"));
    }

    [Fact]
    public async Task OnlyTheMeetingsOwnWordsAreSentToTheModel()
    {
        var generator = new Generator("Delivery split");
        await new GeneratedMeetingTitler(generator).SuggestAsync(["KX-4402 tolerance", "ISO 7599"], CancellationToken.None);

        Assert.Contains("KX-4402 tolerance", generator.Prompt);
        Assert.Contains("ISO 7599", generator.Prompt);
    }

    [Fact]
    public async Task ALongMeetingIsSummarisedFromItsOpeningNotItsWholeTranscript()
    {
        var generator = new Generator("Delivery split");
        string[] lines = [.. Enumerable.Range(1, 200).Select(i => $"line {i}")];
        await new GeneratedMeetingTitler(generator).SuggestAsync(lines, CancellationToken.None);

        Assert.Contains("line 1", generator.Prompt);
        Assert.DoesNotContain("line 200", generator.Prompt);
    }
}
