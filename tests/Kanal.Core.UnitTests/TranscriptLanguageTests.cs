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

    [Fact]
    public void AScriptOutsideTheRoomFallsBackToTheRoom() =>
        Assert.Equal("de", TranscriptLanguage.Guess("这批支架的料号", ["de", "pl"]));

    [Theory]
    [InlineData("If you want to try this, you gotta be really sure about it", "en")]
    [InlineData("Musimy potwierdzić termin", "pl")]
    public void ALatinLanguageOutsideTheRoomIsStillRecognised(string text, string expected) =>
        Assert.Equal(expected, TranscriptLanguage.Guess(text, expected == "en" ? Room : ["en"]));

    [Fact]
    public void TextWithNoSignalGoesToTheFirstLatinLanguageOfTheRoom() =>
        Assert.Equal("de", TranscriptLanguage.Guess("KX-4402 ISO 7599", Room));
}
