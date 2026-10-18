using Kanal.AsrBenchmark;

namespace Kanal.Core.UnitTests;

public class AsrBenchmarkCorpusTests
{
    [Fact]
    public void A_text_grid_gives_one_interval_per_text_line_with_its_tier()
    {
        const string grid = """
            File type = "ooTextFile"
            xmin = 0
            xmax = 100
            item [1]:
                class = "IntervalTier"
                xmin = 0
                xmax = 100
                intervals [1]:
                    xmin = 0
                    xmax = 4.5
                    text = "你好"
                intervals [2]:
                    xmin = 4.5
                    xmax = 9
                    text = ""
            item [2]:
                class = "IntervalTier"
                intervals [1]:
                    xmin = 1.25
                    xmax = 2
                    text = "<noise>"
            """;

        Assert.Equal(
            [(1, 0.0, 4.5, "你好"), (1, 4.5, 9.0, ""), (2, 1.25, 2.0, "<noise>")],
            Corpus.TextGrid(grid));
    }

    [Theory]
    [InlineData("<sil>你好 ", "你好")]
    [InlineData("你<a>好", "你好")]
    [InlineData("<noise>", "")]
    public void Clean_removes_angle_bracket_tags(string text, string expected)
    {
        Assert.Equal(expected, Corpus.Clean(text));
    }

    [Fact]
    public void Aishell4_keeps_clean_single_speaker_intervals_of_3_to_20_seconds()
    {
        (int, double, double, string)[] intervals =
        [
            (1, 0, 4, "甲"),
            (2, 10, 12, "乙"),
            (1, 20, 25, "丙"),
            (2, 22, 23, "丁"),
            (1, 30, 35, "<noise>"),
            (2, 40, 45, "戊"),
            (1, 50, 80, "长"),
            (1, 60, 63, "己"),
            (2, 61, 62, "  "),
        ];

        Assert.Equal(["甲", "戊", "己"], Corpus.PickAishell4(intervals, 10).Select(i => i.Text));
        Assert.Equal(["甲"], Corpus.PickAishell4(intervals, 1).Select(i => i.Text));
        Assert.Empty(Corpus.PickAishell4([], 5));
    }

    [Fact]
    public void Ami_keeps_segments_of_3_to_20_seconds_that_overlap_no_other_segment()
    {
        (string, double, double, string, string)[] segments =
        [
            ("a", 0, 5, "x", "s"),
            ("b", 4, 9, "y", "s"),
            ("c", 20, 22, "z", "s"),
            ("d", 30, 40, "z", "s"),
            ("e", 50, 75, "z", "s"),
            ("f", 100, 104, "z", "s"),
            ("g", 200, 203, "z", "s"),
            ("h", 300, 320, "z", "s"),
        ];

        Assert.Equal(["d", "f", "g", "h"], Corpus.PickAmi(segments, 10).Select(s => s.Id));
        Assert.Equal(["d", "f"], Corpus.PickAmi(segments, 2).Select(s => s.Id));
    }

    [Fact]
    public void A_partial_fetch_keeps_the_clips_of_the_known_sets_it_did_not_fetch()
    {
        string[] old =
        [
            "fleurs	zh	a	fleurs/zh/a.wav	one",
            "ami	en	b	ami/en/b.wav	two",
            "gone	en	c	gone/en/c.wav	three",
            "meeting-piper	de	d	meeting-piper/d.wav	four",
        ];

        Assert.Equal([old[0], old[3]], Corpus.KeepOtherSets(old, ["ami"]));
        Assert.Equal([old[0], old[1], old[3]], Corpus.KeepOtherSets(old, ["commonvoice"]));
    }

    private static (string Id, string Language, double Seconds, string Text, long Session, long Speaker, string Src) AscendRow(
        string id, string language = "mixed", double seconds = 5, string text = "你好 hello", long session = 1, long speaker = 1) =>
        (id, language, seconds, text, session, speaker, "s");

    [Fact]
    public void Ascend_keeps_clips_of_the_language_that_last_3_to_20_seconds_and_have_a_clean_reference()
    {
        var rows = new[]
        {
            AscendRow("a"),
            AscendRow("b", language: "zh"),
            AscendRow("c", seconds: 2.9),
            AscendRow("d", seconds: 3),
            AscendRow("e", seconds: 20),
            AscendRow("f", seconds: 20.1),
            AscendRow("g", text: "你好 [UNK] hello"),
            AscendRow("h", text: "  "),
        };

        Assert.Equal(["a", "d", "e"], Corpus.PickAscend(rows, "mixed", 10).Select(r => r.Id));
        Assert.Equal(["b"], Corpus.PickAscend(rows, "zh", 10).Select(r => r.Id));
    }

    [Fact]
    public void Ascend_spreads_the_pick_over_the_speaker_and_session_groups_in_even_steps()
    {
        var rows = Enumerable.Range(1, 6).Select(i => AscendRow($"a{i}"))
            .Concat([AscendRow("b1", session: 2), AscendRow("b2", session: 2)])
            .Reverse()
            .ToArray();

        Assert.Equal(["a1", "a4", "b1", "b2"], Corpus.PickAscend(rows, "mixed", 4).Select(r => r.Id));
        Assert.Equal(["a1", "a3", "a5", "b1", "b2"], Corpus.PickAscend(rows, "mixed", 5).Select(r => r.Id));
    }

    [Fact]
    public void Ascend_returns_every_clip_when_fewer_than_asked_for_remain()
    {
        var rows = new[] { AscendRow("a"), AscendRow("b", speaker: 2), AscendRow("c", speaker: 2) };

        Assert.Equal(["a", "b", "c"], Corpus.PickAscend(rows, "mixed", 60).Select(r => r.Id));
        Assert.Empty(Corpus.PickAscend(rows, "en", 15));
    }

    [Theory]
    [InlineData("mixed", "zh")]
    [InlineData("zh", "zh")]
    [InlineData("en", "en")]
    [InlineData("de", "de")]
    public void A_code_switched_clip_gets_a_Chinese_room_when_the_room_is_forced(string clipLanguage, string room)
    {
        Assert.Equal(room, Corpus.RoomLanguage(clipLanguage));
    }
}
