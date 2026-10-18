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
}
