using Kanal.AsrBenchmark;

namespace Kanal.Core.UnitTests;

public class AsrBenchmarkE2eTests
{
    private static Clip ClipOf(string id) =>
        new("meeting-11labs", id[..2], id, E2e.Reference(id, id[..2]), []);

    private static E2eRun Run(string route, string id, string transcript, Dictionary<string, string> translations, double? latency = 1) =>
        new(route, ClipOf(id), transcript, [id[..2]], translations, latency);

    private static Dictionary<string, string> Exact(string id) =>
        E2e.Targets(id[..2]).ToDictionary(l => l, l => E2e.Reference(id, l));

    [Fact]
    public void ChrF_is_100_for_an_exact_match_and_0_for_no_shared_character()
    {
        Assert.Equal(100, Scoring.ChrF("Spoina pęka.", "Spoina  pęka."), 6);
        Assert.Equal(0, Scoring.ChrF("abc", "xyz"));
        Assert.Equal(0, Scoring.ChrF("", "xyz"));
    }

    [Fact]
    public void ChrF_ranks_a_close_translation_above_a_distant_one()
    {
        const string reference = "Die Schweißnaht am Halter zeigt Risse.";
        Assert.True(Scoring.ChrF("Die Schweißnaht am Halter hat Risse.", reference)
                    > Scoring.ChrF("Der Träger ist gebrochen.", reference));
    }

    [Fact]
    public void The_meeting_sentences_are_parallel_in_all_four_languages()
    {
        foreach (var lang in Corpus.Languages)
            Assert.Equal(12, Corpus.Meeting.Count(m => m.Lang == lang));

        for (var i = 1; i <= 12; i++)
        {
            var codes = Corpus.Languages
                .Select(l => string.Join(' ', Scoring.Codes(E2e.Reference($"{l}-{i:D2}-x", l)).Order()))
                .Distinct()
                .ToList();
            Assert.Single(codes);
        }
    }

    [Fact]
    public void The_reference_translation_is_the_target_version_of_the_same_sentence()
    {
        Assert.Equal(Corpus.Meeting.Where(m => m.Lang == "zh").ElementAt(2).Text, E2e.Reference("de-03-alice", "zh"));
        Assert.Equal(Corpus.Meeting.Where(m => m.Lang == "en").ElementAt(11).Text, E2e.Reference("pl-12-george", "en"));
        Assert.Equal(7, E2e.SentenceIndex("en-07-lily"));
    }

    [Fact]
    public void A_clip_is_translated_into_the_other_three_room_languages()
    {
        Assert.Equal(["zh", "pl", "en"], E2e.Targets("de"));
        Assert.Equal(["de", "pl", "en"], E2e.Targets("zh"));
    }

    [Fact]
    public void A_missing_translation_scores_zero_and_loses_its_part_numbers()
    {
        var run = Run("a", "de-01-lily", "Die Schweißnaht am Halter KB-4471",
            new() { ["zh"] = E2e.Reference("de-01-lily", "zh"), ["en"] = "Nothing in common here" });

        var pairs = E2e.Pairs(run);

        Assert.Equal(["zh", "pl", "en"], pairs.Select(p => p.Target));
        Assert.Equal(100, pairs[0].ChrF, 6);
        Assert.Equal((1, 1, 1), (pairs[0].Codes, pairs[0].Strict, pairs[0].Loose));
        Assert.Equal(0, pairs[1].ChrF);
        Assert.Equal((1, 0, 0), (pairs[1].Codes, pairs[1].Strict, pairs[1].Loose));
        Assert.True(pairs[2].ChrF < 50);
    }

    [Fact]
    public void The_loose_part_number_count_forgives_case_and_separators()
    {
        var run = Run("a", "zh-04-alice", "", new()
        {
            ["de"] = "Bitte S235 JR auf x5crni18 10 umstellen, IP67",
            ["pl"] = E2e.Reference("zh-04-alice", "pl"),
            ["en"] = "",
        });

        var de = E2e.Pairs(run).Single(p => p.Target == "de");

        Assert.Equal((3, 1, 3), (de.Codes, de.Strict, de.Loose));
    }

    [Fact]
    public void The_summary_averages_chrF_and_adds_part_numbers_per_route_and_language_pair()
    {
        E2eRun[] runs =
        [
            Run("a", "de-01-lily", "", Exact("de-01-lily")),
            Run("a", "de-02-lily", "", new()),
            Run("b", "de-01-lily", "", Exact("de-01-lily")),
        ];

        var rows = E2e.Summarise(runs);
        RouteSummary Row(string route, string source, string target) =>
            rows.Single(r => r.Route == route && r.Source == source && r.Target == target);

        Assert.Equal(50, Row("a", "de", "zh").ChrF, 6);
        Assert.Equal(2, Row("a", "de", "zh").Pairs);
        Assert.Equal(100, Row("b", "all", "all").ChrF, 6);
        Assert.Equal(6, Row("a", "all", "all").Pairs);
        Assert.Equal(
            Row("a", "all", "all").Strict,
            rows.Where(r => r is { Route: "a", Source: not "all", Target: not "all" }).Sum(r => r.Strict));
    }

    [Fact]
    public void The_transcription_error_rate_uses_number_normalised_scoring()
    {
        var exact = Run("c", "de-06-lily", E2e.Reference("de-06-lily", "de").Replace("5000", "fünftausend"), new());
        var wrong = Run("c", "de-06-lily", "Können Sie uns ein Angebot schicken", new());

        Assert.Equal(0, E2e.AsrErrorPct([exact]));
        Assert.True(E2e.AsrErrorPct([wrong]) > 30);
    }

    [Fact]
    public void The_latency_summary_skips_clips_without_a_translation_and_uses_the_nearest_rank()
    {
        E2eRun[] runs =
        [
            Run("a", "de-01-lily", "", new(), 1),
            Run("a", "de-02-lily", "", new(), 3),
            Run("a", "de-03-lily", "", new(), null),
            Run("a", "pl-01-lily", "", new(), 9),
        ];

        var latency = E2e.Latency(runs);

        Assert.Equal((3, 3, 9), (latency.N, latency.P50, latency.P95));
    }

    [Fact]
    public void The_report_has_one_row_per_route_with_a_dash_for_a_missing_figure()
    {
        E2eRun[] runs =
        [
            Run("route-a", "de-01-lily", "x", new() { ["zh"] = E2e.Reference("de-01-lily", "zh") }, null),
        ];

        var report = E2eReport.Build(new DateTime(2026, 10, 18), "machine", runs, ["note one"]);

        Assert.Contains("| route-a |", report);
        Assert.Contains("| – |", report);
        Assert.Contains("- note one", report);
    }
}
