using Kanal.Core.Insights;
using Kanal.Core.Models;
using Kanal.Providers.LocalMt;

namespace Kanal.Core.UnitTests;

public class GeneratedMeetingAgentTests
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

    private static Utterance Final(string id, string lang, string text) =>
        new(id, "S01", 0, 1000, lang, text, 1, UtteranceState.Final, false, 0.9, new Dictionary<string, string>());

    private static readonly MeetingAgentWindow Window = new(
        [
            Final("u1", "pl", "Musimy potwierdzić termin dostawy przed końcem sierpnia."),
            Final("u2", "zh", "样品下周一寄出。"),
        ],
        ["Lieferung"],
        "de");

    private static Task<IReadOnlyList<MeetingInsight>> Propose(Generator generator) =>
        new GeneratedMeetingAgent(generator).ProposeAsync(Window, TestContext.Current.CancellationToken);

    [Fact]
    public async Task ThePromptNumbersEachFinalAndCarriesTheKnownTopicsAndTheReplyLanguage()
    {
        var generator = new Generator("NONE");
        await Propose(generator);

        Assert.Contains("[1] pl: Musimy potwierdzić termin dostawy przed końcem sierpnia.", generator.Prompt);
        Assert.Contains("[2] zh: 样品下周一寄出。", generator.Prompt);
        Assert.DoesNotContain("u1", generator.Prompt);
        Assert.Contains("Lieferung", generator.Prompt);
        Assert.Contains("\"de\"", generator.Prompt);
    }

    [Fact]
    public async Task EachWellFormedLineBecomesOneCandidate()
    {
        var found = await Propose(new Generator(
            "DECISION | Lieferung | Liefertermin vor Ende August bestätigen | 1\n" +
            "point|Muster|Muster gehen am Montag raus|2, 1"));

        Assert.Equal(2, found.Count);
        Assert.Equal(InsightKind.Decision, found[0].Kind);
        Assert.Equal("Lieferung", found[0].Topic);
        Assert.Equal("Liefertermin vor Ende August bestätigen", found[0].Text);
        Assert.Equal(["u1"], found[0].SourceUtteranceIds);
        Assert.Equal(InsightKind.Point, found[1].Kind);
        Assert.Equal(["u2", "u1"], found[1].SourceUtteranceIds);
    }

    [Fact]
    public async Task ChatterAroundTheLinesAndMalformedLinesAreIgnored()
    {
        var found = await Propose(new Generator(
            "<think>the delivery date</think>Here are the items:\n" +
            "PROPOSAL|Lieferung|Expressversand|1\n" +
            "IDEA|Lieferung|Something|1\n" +
            "POINT|Lieferung|No sources|\n" +
            "POINT|Lieferung|A line that does not exist|9\n" +
            "POINT||No topic|1\n" +
            "NONE"));

        var only = Assert.Single(found);
        Assert.Equal(InsightKind.Proposal, only.Kind);
        Assert.Equal("Expressversand", only.Text);
    }

    [Fact]
    public async Task ABulletedOrNumberedReplyWithBracketedNumbersStillMapsToTheUtterances()
    {
        var found = await Propose(new Generator(
            "- POINT|Muster|Muster gehen am Montag raus|[2]\n" +
            "1. DECISION|Lieferung|Liefertermin vor Ende August bestätigen|[1], [2]"));

        Assert.Equal(2, found.Count);
        Assert.Equal(["u2"], found[0].SourceUtteranceIds);
        Assert.Equal(InsightKind.Decision, found[1].Kind);
        Assert.Equal(["u1", "u2"], found[1].SourceUtteranceIds);
    }

    [Fact]
    public async Task NoneMeansNothing()
    {
        Assert.Empty(await Propose(new Generator("NONE")));
    }
}
