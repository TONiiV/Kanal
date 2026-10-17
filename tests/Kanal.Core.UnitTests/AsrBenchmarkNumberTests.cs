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
    [InlineData("5 000 sztuk", "5000 sztuk")]
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
