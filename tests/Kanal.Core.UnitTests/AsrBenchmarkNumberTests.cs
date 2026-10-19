using Kanal.AsrBenchmark;

namespace Kanal.Core.UnitTests;

public class AsrBenchmarkNumberTests
{
    [Theory]
    [InlineData("两千次", "2000次")]
    [InlineData("二千次", "2000次")]
    [InlineData("支架四千四百七十一", "支架4471")]
    [InlineData("一万二千", "12000")]
    [InlineData("两千五", "2500")]
    [InlineData("一百零五", "105")]
    [InlineData("十五日", "15日")]
    [InlineData("四百八十小时", "480小时")]
    [InlineData("二零二六年", "2026年")]
    [InlineData("二点五毫米", "2.5毫米")]
    [InlineData("降低百分之三", "降低3%")]
    [InlineData("我们需要重新做疲劳测试", "我们需要重新做疲劳测试")]
    public void Chinese_numerals_become_digits(string text, string expected)
    {
        Assert.Equal(expected, Numbers.Normalize("zh", text));
    }

    [Theory]
    [InlineData("fünftausend Stück", "5000 stück")]
    [InlineData("viertausendvierhunderteinundsiebzig", "4471")]
    [InlineData("vier tausend vier hundert einundsiebzig", "4471")]
    [InlineData("Prüfbericht PB zweitausendzweihunderteinundneunzig", "prüfbericht pb 2291")]
    [InlineData("vierhundertachtzig Stunden", "480 stunden")]
    [InlineData("zwei Millionen dreihunderttausend", "2300000")]
    [InlineData("eine Million", "1000000")]
    [InlineData("null komma drei Millimeter", "0.3 millimeter")]
    [InlineData("um zehn Prozent", "um 10 prozent")]
    [InlineData("ein Angebot für die Stunden", "ein angebot für die stunden")]
    [InlineData("5.000 Stück", "5000 stück")]
    public void German_number_words_become_digits(string text, string expected)
    {
        Assert.Equal(expected, Numbers.Normalize("de", text));
    }

    [Theory]
    [InlineData("dwa tysiące cykli", "2000 cykli")]
    [InlineData("cztery tysiące czterysta siedemdziesiąt jeden", "4471")]
    [InlineData("dwadzieścia pięć Nm", "25 nm")]
    [InlineData("pięć tysięcy sztuk", "5000 sztuk")]
    [InlineData("tysiąc", "1000")]
    [InlineData("dwa przecinek pięć", "2.5")]
    [InlineData("o trzy procent", "o 3 procent")]
    [InlineData("5\u00A0000 sztuk", "5000 sztuk")]
    [InlineData("5\u202F000 sztuk", "5000 sztuk")]
    [InlineData("Czy dostawca może przyspieszyć wysyłkę", "czy dostawca może przyspieszyć wysyłkę")]
    public void Polish_number_words_become_digits(string text, string expected)
    {
        Assert.Equal(expected, Numbers.Normalize("pl", text));
    }

    [Theory]
    [InlineData("five thousand pieces", "5000 pieces")]
    [InlineData("four thousand four hundred and seventy-one", "4471")]
    [InlineData("a hundred", "100")]
    [InlineData("two point five millimetres", "2.5 millimetres")]
    [InlineData("twenty five", "25")]
    [InlineData("5,000 pieces", "5000 pieces")]
    [InlineData("KB 4471 and the rest", "kb 4471 and the rest")]
    [InlineData("we need the drawing in revision C", "we need the drawing in revision c")]
    public void English_number_words_become_digits(string text, string expected)
    {
        Assert.Equal(expected, Numbers.Normalize("en", text));
    }

    [Theory]
    [InlineData("pl", "15 300", "15 300")]
    [InlineData("en", "M10 100", "m10 100")]
    [InlineData("en", "1.250 mm", "1 250 mm")]
    [InlineData("de", "2,500 mm", "2 500 mm")]
    [InlineData("en", "5,000 pieces", "5000 pieces")]
    [InlineData("de", "5.000 Stück", "5000 stück")]
    public void Only_the_thousands_separator_of_the_language_joins_digit_groups(string lang, string text, string expected)
    {
        Assert.Equal(expected, Numbers.Normalize(lang, text));
    }

    [Theory]
    [InlineData("po dwóch tysiącach cykli", "po 2000 cykli")]
    [InlineData("z trzema tysiącami sztuk", "z 3000 sztuk")]
    [InlineData("z pięcioma tysiącami sztuk", "z 5000 sztuk")]
    [InlineData("z dwoma milionami", "z 2000000")]
    [InlineData("z jednego miliona", "z 1000000")]
    [InlineData("jednym z nas", "jednym z nas")]
    [InlineData("o trzy procenty", "o 3 procent")]
    [InlineData("w trzech procentach", "w 3 procent")]
    [InlineData("procentowy wzrost", "procentowy wzrost")]
    public void Polish_case_forms_of_numbers_and_percent_are_read(string text, string expected)
    {
        Assert.Equal(expected, Numbers.Normalize("pl", text));
    }

    [Theory]
    [InlineData("twelve hundred euros", "1200 euros")]
    [InlineData("fifteen hundred", "1500")]
    [InlineData("twenty five hundred", "2500")]
    public void English_teen_and_tens_hundreds_are_read_as_one_number(string text, string expected)
    {
        Assert.Equal(expected, Numbers.Normalize("en", text));
    }

    [Theory]
    [InlineData("hunderttausend Euro", "100000 euro")]
    [InlineData("einhunderttausend", "100000")]
    [InlineData("zweihunderttausend", "200000")]
    public void German_hunderttausend_is_read(string text, string expected)
    {
        Assert.Equal(expected, Numbers.Normalize("de", text));
    }

    [Theory]
    [InlineData("5万元", "50000元")]
    [InlineData("3千次", "3000次")]
    [InlineData("2.5万", "25000")]
    [InlineData("五万元", "50000元")]
    [InlineData("万一出问题", "万一出问题")]
    [InlineData("千万不要", "千万不要")]
    [InlineData("千万别忘", "千万别忘")]
    [InlineData("万万不可", "万万不可")]
    [InlineData("3千万元", "30000000元")]
    [InlineData("1万亿", "1000000000000")]
    [InlineData("100万元", "1000000元")]
    [InlineData("百万元", "1000000元")]
    [InlineData("千万元", "10000000元")]
    [InlineData("1,000万元", "10000000元")]
    [InlineData("1,000万", "10000000")]
    [InlineData("今年，100万元", "今年,1000000元")]
    [InlineData("1.5万，2万", "15000,20000")]
    public void Chinese_digits_with_a_unit_and_idioms_are_read(string text, string expected)
    {
        Assert.Equal(expected, Numbers.Normalize("zh", text));
    }

    [Theory]
    [InlineData("百度", "100度")]
    [InlineData("百分比", "100分比")]
    public void A_bare_hundred_or_thousand_word_is_read_as_a_number(string text, string expected)
    {
        Assert.Equal(expected, Numbers.Normalize("zh", text));
    }

    [Fact]
    public void Two_numbers_in_a_row_stay_two_numbers()
    {
        Assert.Equal("5 5", Numbers.Normalize("en", "five five"));
        Assert.Equal("15 300", Numbers.Normalize("pl", "piętnaście trzysta"));
        Assert.Equal("20 26", Numbers.Normalize("en", "twenty twenty-six"));
    }

    [Fact]
    public void Number_normalised_scores_ignore_how_a_number_is_written()
    {
        Assert.Equal((0, 5), Scoring.Errors("zh", "两千次", "2000次", numbers: true));
        Assert.Equal((4, 5), Scoring.Errors("zh", "两千次", "2000次"));
        Assert.Equal((0, 2), Scoring.Errors("de", "fünftausend Stück", "5000 Stück", numbers: true));
        Assert.Equal((0, 3), Scoring.Errors("de", "null komma drei Millimeter", "0,3 Millimeter", numbers: true));
        Assert.Equal((0, 2), Scoring.Errors("pl", "dwa tysiące cykli", "2000 cykli", numbers: true));
        Assert.Equal((0, 2), Scoring.Errors("en", "five thousand pieces", "5,000 pieces", numbers: true));
        Assert.Equal((0, 2), Scoring.Errors("en", "three percent", "3%", numbers: true));
    }
}
