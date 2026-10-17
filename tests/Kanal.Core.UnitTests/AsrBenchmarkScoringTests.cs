using Kanal.AsrBenchmark;

namespace Kanal.Core.UnitTests;

public class AsrBenchmarkScoringTests
{
    [Fact]
    public void Chinese_normalisation_drops_punctuation_and_spaces_and_simplifies()
    {
        Assert.Equal("支架kb4471的焊缝出现裂纹", Scoring.NormalizeChinese("支架 KB-4471 的焊縫，出現裂紋。"));
    }

    [Fact]
    public void Word_normalisation_lowercases_and_strips_punctuation()
    {
        Assert.Equal(["die", "bohrung", "für", "m10", "liegt", "0", "3", "mm", "außerhalb"],
            Scoring.Words("Die Bohrung für M10 liegt 0,3 mm  außerhalb."));
        Assert.Equal(["czy", "dostawca", "może", "przyspieszyć"], Scoring.Words("Czy dostawca – może przyspieszyć?"));
    }

    [Fact]
    public void Cer_counts_character_edits_against_the_reference_length()
    {
        Assert.Equal((0, 6), Scoring.Errors("zh", "我们需要测试。", "我们需要测试"));
        Assert.Equal((1, 6), Scoring.Errors("zh", "我们要测试", "我们需要测试"));
        Assert.Equal((1, 6), Scoring.Errors("zh", "我们需要测试了", "我们需要测试"));
        Assert.Equal((1, 6), Scoring.Errors("zh", "我们需要考试", "我们需要测试"));
        Assert.Equal((6, 6), Scoring.Errors("zh", "", "我们需要测试"));
    }

    [Fact]
    public void Wer_counts_word_substitutions_insertions_and_deletions()
    {
        const string reference = "wir brauchen die Zeichnung";
        Assert.Equal((0, 4), Scoring.Errors("de", "Wir brauchen die Zeichnung.", reference));
        Assert.Equal((1, 4), Scoring.Errors("de", "wir brauchen das Zeichnung", reference));
        Assert.Equal((1, 4), Scoring.Errors("de", "wir die Zeichnung", reference));
        Assert.Equal((1, 4), Scoring.Errors("de", "wir brauchen die neue Zeichnung", reference));
        Assert.Equal((2, 4), Scoring.Errors("pl", "musimy powtórzyć", "Musimy powtórzyć test zmęczeniowy."));
    }

    [Fact]
    public void Codes_are_kept_strictly_only_verbatim_and_loosely_without_case_and_separators()
    {
        const string source = "请把材料从 S235JR 换成 X5CrNi18-10。";
        Assert.Equal(["S235JR", "X5CrNi18-10"], Scoring.Codes(source));
        Assert.Equal(2, Scoring.CodesKept(source, "从 S235JR 换成 X5CrNi18-10", loose: false));
        Assert.Equal(1, Scoring.CodesKept(source, "从 S235JR 换成 X5CrNi1810", loose: false));
        Assert.Equal(0, Scoring.CodesKept(source, "从 s235 jr 换成 x5 crni 18 10", loose: false));
        Assert.Equal(2, Scoring.CodesKept(source, "从 s235 jr 换成 x5 crni 18 10", loose: true));
        Assert.Equal(0, Scoring.CodesKept(source, "从钢换成不锈钢", loose: true));
    }

    [Fact]
    public void Percentile_uses_the_nearest_rank()
    {
        double[] values = [5, 1, 4, 2, 3, 6, 7, 8, 9, 10];
        Assert.Equal(5, Scoring.Percentile(values, 0.5));
        Assert.Equal(10, Scoring.Percentile(values, 0.95));
        Assert.Equal(0, Scoring.Percentile([], 0.5));
    }

    [Fact]
    public void Speech_onset_is_the_first_frame_within_20_dB_of_the_loudest_frame()
    {
        var samples = new short[16_000];
        for (var i = 8_000; i < 16_000; i++)
            samples[i] = (short)(i % 2 == 0 ? 8_000 : -8_000);
        samples[100] = 200;
        Assert.Equal(8_000, Scoring.SpeechOnset(samples, 320));
    }
}
