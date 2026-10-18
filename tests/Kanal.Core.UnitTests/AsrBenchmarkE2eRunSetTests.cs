using Kanal.AsrBenchmark;

namespace Kanal.Core.UnitTests;

public class AsrBenchmarkE2eRunSetTests
{
    private static Clip ClipOf(string id) =>
        new("meeting-11labs", id[..2], id, E2e.Reference(id, id[..2]), []);

    private static E2eRun Run(string route, string id, string transcript = "x", Dictionary<string, string>? translations = null,
        double? latency = 1, string[]? langs = null) =>
        new(route, ClipOf(id), transcript, langs ?? [id[..2]], translations ?? Exact(id), latency);

    private static Dictionary<string, string> Exact(string id) =>
        E2e.Targets(id[..2]).ToDictionary(l => l, l => E2e.Reference(id, l));

    private static Dictionary<string, string> Three(string text) =>
        new() { ["zh"] = text, ["pl"] = text, ["en"] = text };

    [Fact]
    public void Join_keeps_the_order_of_the_parts_and_the_room_and_drops_empty_text()
    {
        var joined = E2e.Join([
            new Dictionary<string, string> { ["en"] = "one", ["zh"] = "" },
            new Dictionary<string, string> { ["en"] = "two", ["de"] = "zwei" },
        ]);

        Assert.Equal(["de", "en"], joined.Keys);
        Assert.Equal("one two", joined["en"]);
        Assert.Equal("zwei", joined["de"]);
    }

    [Fact]
    public void Route_A_gets_a_latency_only_when_every_utterance_has_all_three_targets()
    {
        var complete = E2e.RouteA([
            new HeardUtterance("Erster Teil.", "de", 5, 6.5, Three("a")),
            new HeardUtterance("Zweiter Teil.", "de", 6, 7.25, Three("b")),
        ], 4);

        Assert.True(complete.Complete);
        Assert.Equal(3.25, complete.LatencySeconds);
        Assert.Equal("Erster Teil. Zweiter Teil.", complete.Transcript);
        Assert.Equal(["de", "de"], complete.SrcLangs);
        Assert.Equal("a b", complete.Translations["zh"]);

        var late = E2e.RouteA([
            new HeardUtterance("Erster Teil.", "de", 5, 6.5, Three("a")),
            new HeardUtterance("Zweiter Teil.", "de", 6, 6.1, new Dictionary<string, string> { ["zh"] = "b", ["en"] = "b" }),
        ], 4);

        Assert.False(late.Complete);
        Assert.Null(late.LatencySeconds);
        Assert.Equal("a b", late.Translations["zh"]);
        Assert.Equal("a", late.Translations["pl"]);
    }

    [Fact]
    public void Route_A_expects_the_other_room_languages_of_each_utterance_language()
    {
        var zh = E2e.RouteA([new HeardUtterance("你好", "zh", 1, 2, new Dictionary<string, string> { ["de"] = "a", ["pl"] = "a", ["en"] = "a" })], 0);
        var none = E2e.RouteA([], 0);

        Assert.True(zh.Complete);
        Assert.Equal(2, zh.LatencySeconds);
        Assert.False(none.Complete);
        Assert.Null(none.LatencySeconds);
    }

    [Fact]
    public void A_run_is_complete_when_all_three_targets_have_text()
    {
        Assert.True(E2e.IsComplete(Run("a", "de-01-lily")));
        Assert.False(E2e.IsComplete(Run("a", "de-01-lily", translations: new() { ["zh"] = "a", ["pl"] = "", ["en"] = "c" })));
        Assert.False(E2e.IsComplete(Run("a", "de-01-lily", translations: new() { ["de"] = "a", ["zh"] = "a", ["en"] = "c" })));
    }

    [Fact]
    public void The_complete_only_latency_skips_runs_with_a_missing_target()
    {
        E2eRun[] runs =
        [
            Run("a", "de-01-lily", latency: 2),
            Run("a", "de-02-lily", translations: new() { ["zh"] = "a" }, latency: 9),
        ];

        Assert.Equal(1, E2e.Latency(runs, completeOnly: true).N);
        Assert.Equal(2.0, E2e.Latency(runs, completeOnly: true).P95);
        Assert.Equal(2, E2e.Latency(runs).N);
    }

    [Fact]
    public void A_rerun_replaces_the_earlier_run_of_the_same_route_and_clip_and_carries_the_mark()
    {
        E2eRun[] earlier = [Run("c", "de-01-lily", "old", latency: 40), Run("c", "de-02-lily", "keep"), Run("d", "de-01-lily", "other route")];
        E2eRun[] fresh = [Run("c", "de-01-lily", "new", latency: 5)];

        var merged = E2e.Merge(earlier, fresh);

        var replaced = merged.Runs.Single(r => r.Route == "c" && r.Clip.Id == "de-01-lily");
        Assert.Equal("new", replaced.Transcript);
        Assert.True(replaced.Rerun);
        Assert.False(merged.Runs.Single(r => r.Clip.Id == "de-02-lily").Rerun);
        Assert.Equal("other route", merged.Runs.Single(r => r.Route == "d").Transcript);
        Assert.Empty(merged.KeptEarlier);
    }

    [Fact]
    public void After_a_merge_each_route_and_clip_appears_once_and_a_new_clip_is_added()
    {
        E2eRun[] earlier = [Run("c", "de-01-lily"), Run("c", "de-02-lily")];
        E2eRun[] fresh = [Run("c", "de-02-lily"), Run("c", "de-03-lily")];

        var merged = E2e.Merge(earlier, fresh);

        Assert.Equal(["de-01-lily", "de-02-lily", "de-03-lily"], merged.Runs.Select(r => r.Clip.Id));
        Assert.False(merged.Runs[2].Rerun);
        Assert.True(merged.Runs[1].Rerun);
    }

    [Fact]
    public void A_clip_that_failed_again_keeps_its_earlier_run_and_is_listed()
    {
        E2eRun[] earlier = [Run("c", "de-01-lily", "good")];
        E2eRun[] fresh = [Run("c", "de-01-lily", "", new(), null)];

        var merged = E2e.Merge(earlier, fresh);

        Assert.Equal("good", Assert.Single(merged.Runs).Transcript);
        Assert.False(merged.Runs[0].Rerun);
        Assert.Equal(["c de-01-lily"], merged.KeptEarlier);
    }

    [Fact]
    public void The_only_option_matches_whole_clip_ids()
    {
        string[] ids = ["zh-01-alice", "zh-01-lily", "zh-10-alice", "de-01-alice"];

        Assert.Equal(["zh-01-lily", "de-01-alice"], E2e.Only(ids, ["zh-01-lily", "de-01-alice"]));
        Assert.Empty(E2e.Only(ids, ["zh-01"]));
        Assert.Empty(E2e.Only(ids, ["zh-1"]));
        Assert.Equal(ids, E2e.Only(ids, null).ToArray());
    }

    [Fact]
    public void The_label_summary_counts_finals_and_the_share_in_the_clip_language()
    {
        E2eRun[] runs =
        [
            Run("a", "de-01-lily", langs: ["de", "de"]),
            Run("a", "de-02-lily", langs: ["de", "en"]),
            Run("a", "pl-01-lily", langs: ["pl"]),
            Run("b", "pl-01-lily", langs: ["en"]),
        ];

        var labels = E2e.Labels(runs.Where(r => r.Route == "a"));

        Assert.Equal(3, labels.Clips);
        Assert.Equal(5, labels.Finals);
        Assert.Equal(5.0 / 3, labels.PerClip, 6);
        Assert.Equal(80, labels.CorrectPct, 6);
    }

    [Fact]
    public void The_transcript_length_is_the_mean_number_of_characters_and_words()
    {
        E2eRun[] runs = [Run("a", "de-01-lily", "eins zwei"), Run("a", "de-02-lily", "eins zwei drei vier")];

        Assert.Equal(14.0, E2e.TranscriptLength(runs).Characters);
        Assert.Equal(3, E2e.TranscriptLength(runs).Words);
    }

    [Fact]
    public void The_load_summary_ignores_runs_without_a_measurement()
    {
        E2eRun[] runs = [Run("a", "de-01-lily") with { OtherLoad = 1 }, Run("a", "de-02-lily") with { OtherLoad = 3 }, Run("a", "de-03-lily")];

        var load = E2e.OtherLoad(runs);

        Assert.Equal((2, 2.0, 3.0), (load.N, load.Mean, load.Max));
    }

    [Fact]
    public void The_report_shows_the_rerun_mark_the_label_summary_and_the_missing_target_count()
    {
        E2eRun[] runs =
        [
            Run("route-a", "de-01-lily") with { Rerun = true, OtherLoad = 0.5 },
            Run("route-a", "de-02-lily", translations: new() { ["zh"] = "a" }, latency: null),
        ];

        var report = E2eReport.Build(new DateTime(2026, 10, 18), "machine", runs, []);

        Assert.Contains("| route-a | de-01-lily |", report);
        Assert.Contains("| yes |", report);
        Assert.Contains("## Transcripts and labels", report);
        Assert.Contains("| route-a | 1/2 |", report);
    }
}
