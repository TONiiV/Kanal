using Kanal.Benchmark;

namespace Kanal.Core.UnitTests;

public class BenchmarkScoringTests
{
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
    public void Codes_are_tokens_with_an_uppercase_letter_and_a_digit()
    {
        Assert.Equal(["S235JR", "X5CrNi18-10", "IP67"],
            Scoring.Codes("请把材料从 S235JR 换成 X5CrNi18-10，客户要求防护等级达到 IP67。"));
        Assert.Empty(Scoring.Codes("uszczelki O-ring, 2000 cykli, Revision C"));
    }

    [Fact]
    public void KeepsCodes_needs_every_code_verbatim()
    {
        Assert.True(Scoring.KeepsCodes("Halter KB-4471, Charge L-2418", "支架 KB-4471，批次 L-2418"));
        Assert.False(Scoring.KeepsCodes("Halter KB-4471", "支架 KB 4471"));
    }

    [Fact]
    public void LeaksScript_flags_CJK_in_European_output_and_no_CJK_in_Chinese_output()
    {
        Assert.True(Scoring.LeaksScript("de", "Halter 支架"));
        Assert.False(Scoring.LeaksScript("pl", "Spoina pęka"));
        Assert.True(Scoring.LeaksScript("zh", "Die Schweißnaht"));
        Assert.False(Scoring.LeaksScript("zh", "支架 KB-4471"));
        Assert.False(Scoring.LeaksScript("zh", ""));
    }

    [Fact]
    public void Percentile_uses_the_nearest_rank()
    {
        double[] values = [5, 1, 4, 2, 3, 6, 7, 8, 9, 10];
        Assert.Equal(5, Scoring.Percentile(values, 0.5));
        Assert.Equal(10, Scoring.Percentile(values, 0.95));
    }
}
