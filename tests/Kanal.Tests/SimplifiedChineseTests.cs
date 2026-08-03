using Kanal.Core.Text;

namespace Kanal.Tests;

/// <summary>
/// The mainland supplier is the primary participant; Gladia only knows a single
/// "zh" and tends to emit Traditional characters. Everything Chinese is
/// normalized to Simplified on the host before it enters room state or relay.
/// </summary>
public class SimplifiedChineseTests
{
    [Theory]
    [InlineData("這是一個測試", "这是一个测试")]
    [InlineData("傳輸控制", "传输控制")]
    [InlineData("頭髮", "头发")] // 髮 is a one-to-many source char in the other direction — must land on 发
    [InlineData("乾燥", "干燥")] // 乾 maps to "干 乾" in OpenCC; the first (most common) mapping wins
    public void TraditionalBecomesSimplified(string traditional, string simplified)
    {
        Assert.Equal(simplified, SimplifiedChinese.Normalize(traditional));
    }

    [Fact]
    public void MixedScriptKeepsPartNumbersIntact()
    {
        Assert.Equal(
            "料号 KX-4402 已确认,公差按 ISO 7599。",
            SimplifiedChinese.Normalize("料號 KX-4402 已確認,公差按 ISO 7599。"));
    }

    /// <summary>Partials arrive many times a second; text that needs no change must
    /// come back as the same instance, so the common case allocates nothing.</summary>
    [Theory]
    [InlineData("这批支架的料号是 KX-4402。")] // already Simplified
    [InlineData("Musimy potwierdzić termin dostawy wsporników — ą ę ł ś ż.")]
    [InlineData("Die Toleranzen für KX-4402 sind freigegeben.")]
    [InlineData("")]
    public void TextThatNeedsNoChangeIsReturnedAsIs(string text)
    {
        Assert.Same(text, SimplifiedChinese.Normalize(text));
    }

    [Fact]
    public void SurrogatePairsSurviveNormalization()
    {
        Assert.Equal("𝄞 测试", SimplifiedChinese.Normalize("𝄞 測試"));
    }

    [Theory]
    [InlineData("zh", true)]
    [InlineData("ZH", true)]
    [InlineData("zh-TW", true)]
    [InlineData("de", false)]
    [InlineData("pl", false)]
    [InlineData(null, false)]
    public void OnlyChineseLanguageCodesAreTargeted(string? code, bool expected)
    {
        Assert.Equal(expected, SimplifiedChinese.IsChinese(code));
    }
}
