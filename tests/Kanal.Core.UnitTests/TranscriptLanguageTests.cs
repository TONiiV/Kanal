using Kanal.Providers.LocalAsr;

namespace Kanal.Core.UnitTests;

public class TranscriptLanguageTests
{
    private static readonly string[] Room = ["zh", "de", "pl"];

    [Theory]
    [InlineData("这批支架的料号是 KX-4402，表面处理按上次的标准做。", "zh")]
    [InlineData("Die Toleranzen im Zeichnungssatz sind noch nicht freigegeben.", "de")]
    [InlineData("Wir brauchen außerdem das Erstmusterprüfprotokoll für KX-4402.", "de")]
    [InlineData("Musimy potwierdzić termin dostawy przed końcem sierpnia.", "pl")]
    [InlineData("Czy próbki będą zgodne z normą ISO 7599?", "pl")]
    [InlineData("To jest data dostawy dla tej partii", "pl")]
    [InlineData("Das ist der Termin für die Lieferung", "de")]
    public void TheRoomLanguageIsReadFromTheText(string text, string expected) =>
        Assert.Equal(expected, TranscriptLanguage.Guess(text, Room));

    [Theory]
    [InlineData("这批支架的料号是 KX-4402，表面处理按上次的标准做。", "de", "pl")]
    [InlineData("这批支架的料号是 KX-4402，表面处理按上次的标准做。", "en")]
    [InlineData("这个 bracket 要改", "de", "pl")]
    public void HanTextWithoutKanaIsChineseInAnyRoom(string text, params string[] room) =>
        Assert.Equal("zh", TranscriptLanguage.Guess(text, room));

    [Fact]
    public void KanaTextIsJapaneseInAnyRoom() =>
        Assert.Equal("ja", TranscriptLanguage.Guess("この部品の図面はまだ承認されていません。", ["zh", "de"]));

    [Fact]
    public void HangulTextIsKoreanInAnyRoom() =>
        Assert.Equal("ko", TranscriptLanguage.Guess("이 부품의 도면은 아직 승인되지 않았습니다.", ["de"]));

    [Fact]
    public void ALatinTextWithNoSignalInARoomWithoutLatinIsEnglish() =>
        Assert.Equal("en", TranscriptLanguage.Guess("Okay, thank you.", ["zh"]));

    [Fact]
    public void ALatinTextWithNoSignalInATwoLanguageRoomWithoutLatinIsEnglish() =>
        Assert.Equal("en", TranscriptLanguage.Guess("Okay, thank you.", ["zh", "ja"]));

    [Fact]
    public void ALatinTextInARoomWithoutLatinIsLabelledByItsBestLanguage() =>
        Assert.Equal("de", TranscriptLanguage.Guess("Die Toleranzen im Zeichnungssatz sind noch nicht freigegeben.", ["zh"]));

    [Fact]
    public void OneSignalIsEnoughInARoomWithoutLatin() =>
        Assert.Equal("de", TranscriptLanguage.Guess("Ich komme gleich.", ["zh"]));

    [Fact]
    public void ALatinLanguageOutsideTheRoomIsStillRecognised() =>
        Assert.Equal("en", TranscriptLanguage.Guess("If you want to try this, you gotta be really sure about it", Room));

    [Theory]
    [InlineData("In Ordnung.", "de")]
    [InlineData("Hast du meine E-Mail bekommen?", "de")]
    public void OneSignalForALanguageOutsideTheRoomDoesNotBeatTheRoom(string text, string expected) =>
        Assert.Equal(expected, TranscriptLanguage.Guess(text, Room));

    [Fact]
    public void TextWithNoSignalGoesToTheFirstLatinLanguageOfTheRoom() =>
        Assert.Equal("de", TranscriptLanguage.Guess("KX-4402 ISO 7599", Room));
}
