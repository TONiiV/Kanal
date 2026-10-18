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
    public void Loose_code_check_does_not_join_neighbouring_words()
    {
        Assert.Equal(0, Scoring.CodesKept("Schraube M10", "zum 10", loose: true));
        Assert.Equal(0, Scoring.CodesKept("Schraube M10", "M100", loose: true));
        Assert.Equal(1, Scoring.CodesKept("Schraube M10", "Schraube M 10.", loose: true));
    }

    [Fact]
    public void Full_width_text_counts_as_ascii()
    {
        Assert.Equal("2000次", Scoring.NormalizeChinese("２０００次"));
        Assert.Equal(1, Scoring.CodesKept("支架 KB-4471", "支架 ＫＢ－４４７１", loose: true));
        Assert.Equal(1, Scoring.CodesKept("支架 KB-4471", "支架 ＫＢ－４４７１", loose: false));
    }

    [Fact]
    public void Decomposed_polish_letters_stay_in_one_word()
    {
        Assert.Equal(["mąka"], Scoring.Words("mąka"));
        Assert.Equal((0, 1), Scoring.Errors("pl", "mąka", "mąka"));
    }

    [Fact]
    public void A_reference_has_a_digit_when_it_holds_any_number_character()
    {
        Assert.True(Scoring.HasDigit("KB-4471"));
        Assert.True(Scoring.HasDigit("２０００次"));
        Assert.False(Scoring.HasDigit("Die Bohrung liegt außerhalb."));
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

    [Fact]
    public void Speech_end_is_the_end_of_the_last_frame_within_20_dB_of_the_loudest_frame()
    {
        var samples = new short[16_000];
        for (var i = 3_200; i < 8_000; i++)
            samples[i] = (short)(i % 2 == 0 ? 8_000 : -8_000);
        samples[12_000] = 200;
        Assert.Equal(8_000, Scoring.SpeechEnd(samples, 320));
    }

    [Theory]
    [InlineData("language Chinese<asr_text>你好", "你好")]
    [InlineData("language English<asr_text> hello ", "hello")]
    [InlineData("language None<asr_text>", "")]
    [InlineData(" 你好 ", "你好")]
    public void The_qwen3_header_is_removed_from_the_text(string text, string expected)
    {
        Assert.Equal(expected, Scoring.StripAsrHeader(text));
    }

    [Fact]
    public void Mixed_tokens_are_one_per_Chinese_character_and_one_per_Latin_word()
    {
        Assert.Equal(["呃", "what", "s", "the", "呃"], Scoring.MixedTokens("呃what's the呃"));
        Assert.Equal(["我", "的", "parents", "是", "chemistry", "老", "师"], Scoring.MixedTokens("我的 Parents 是 chemistry老师。"));
        Assert.Equal(["2000", "次"], Scoring.MixedTokens("2000次"));
        Assert.Empty(Scoring.MixedTokens(" ，。 "));
    }

    [Fact]
    public void Mixed_tokens_read_traditional_characters_and_full_width_letters_like_the_plain_forms()
    {
        Assert.Equal(["学", "校"], Scoring.MixedTokens("學校"));
        Assert.Equal(["ism", "课"], Scoring.MixedTokens("ＩＳＭ課"));
    }

    [Fact]
    public void Mer_counts_edits_over_characters_and_words_together()
    {
        const string reference = "我的 parents 是 chemistry 老师";
        Assert.Equal((0, 7), Scoring.MixedErrors("我的parents是chemistry老师。", reference));
        Assert.Equal((1, 7), Scoring.MixedErrors("我的 parent 是 chemistry 老师", reference));
        Assert.Equal((1, 7), Scoring.MixedErrors("我的 parents 是 chemistry 老", reference));
        Assert.Equal((7, 7), Scoring.MixedErrors("", reference));
    }

    [Fact]
    public void Mer_counts_an_English_word_that_became_Chinese_as_one_substitution_plus_one_insertion()
    {
        Assert.Equal((2, 4), Scoring.MixedErrors("我喜欢化学", "我喜欢 chemistry"));
    }

    [Fact]
    public void Mer_applies_the_number_normalisation_to_both_texts_only_when_asked()
    {
        Assert.Equal((2, 3), Scoring.MixedErrors("两千次 pieces", "2000次 pieces"));
        Assert.Equal((0, 3), Scoring.MixedErrors("两千次 pieces", "2000次 pieces", numbers: true));
        Assert.Equal((0, 4), Scoring.MixedErrors("我要 five thousand pieces", "我要 5,000 pieces", numbers: true));
    }

    [Fact]
    public void English_kept_counts_the_reference_words_that_the_hypothesis_spells_in_Latin_script()
    {
        const string reference = "我的 parents 是 chemistry 老师 and history";
        Assert.Equal((4, 4), Scoring.EnglishKept("我的 Parents 是 Chemistry 老师 and history", reference));
        Assert.Equal((2, 4), Scoring.EnglishKept("我的 parents 是 化学 老师 and", reference));
        Assert.Equal((0, 4), Scoring.EnglishKept("我的父母是化学老师和历史", reference));
        Assert.Equal((0, 4), Scoring.EnglishKept("", reference));
    }

    [Fact]
    public void English_kept_matches_each_hypothesis_word_once_and_ignores_numbers()
    {
        Assert.Equal((2, 4), Scoring.EnglishKept("The cat", "the cat the dog"));
        Assert.Equal((1, 1), Scoring.EnglishKept("2000 bolts", "有 2000 个 bolts"));
        Assert.Equal((0, 0), Scoring.EnglishKept("我们需要测试", "我们需要测试 2000"));
    }

    [Fact]
    public void English_kept_leaves_out_fillers()
    {
        Assert.Equal((1, 1), Scoring.EnglishKept("chemistry", "um 我喜欢 uh chemistry hmm mm Erm"));
        Assert.Equal((0, 0), Scoring.EnglishKept("", "um uh"));
    }

    [Fact]
    public void English_kept_leaves_out_spelled_letters_but_keeps_a_lone_single_letter_word()
    {
        Assert.Equal((0, 0), Scoring.EnglishKept("ISM", "那个 i s m"));
        Assert.Equal((1, 1), Scoring.EnglishKept("ISM information", "那个 i s m information"));
        Assert.Equal((4, 4), Scoring.EnglishKept("I like a plan", "I like a plan"));
    }

    [Fact]
    public void Mer_reads_traditional_characters_before_it_normalises_numbers()
    {
        Assert.Equal((0, 3), Scoring.MixedErrors("兩千次 pieces", "两千次 pieces", numbers: true));
    }
}
