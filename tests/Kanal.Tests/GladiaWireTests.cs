using Kanal.Core.Providers;
using Kanal.Providers.Gladia;

namespace Kanal.Tests;

public class GladiaWireTests
{
    [Fact]
    public void ParsesPartialTranscript()
    {
        var wire = new GladiaWire();

        var events = wire.Parse("""
            {"type":"transcript","data":{"id":"abc","is_final":false,
             "utterance":{"text":"这批支架","language":"zh","start":1.25,"end":2.5,"speaker":0,"confidence":0.87}}}
            """).ToList();

        var t = Assert.IsType<AsrEvent.Transcript>(Assert.Single(events));
        Assert.Equal("abc", t.UtteranceId);
        Assert.False(t.IsFinal);
        Assert.Equal("这批支架", t.Text);
        Assert.Equal("zh", t.SrcLang);
        Assert.Equal(1250, t.TStartMs);
        Assert.Null(t.TEndMs); // end is only trusted on finals
        Assert.Equal("S01", t.SpeakerTag);
        Assert.Equal(0.87, t.SpeakerConfidence, 3);
    }

    [Fact]
    public void TranslationReEmitsCachedTranscriptWithTranslation()
    {
        var wire = new GladiaWire();
        wire.Parse("""
            {"type":"transcript","data":{"id":"abc","is_final":true,
             "utterance":{"text":"料号确认","language":"zh","start":0,"end":1.0}}}
            """).ToList();

        var events = wire.Parse("""
            {"type":"translation","data":{"id":"abc","target_language":"de",
             "translated_utterance":{"text":"Teilenummer bestätigt","language":"de"}}}
            """).ToList();

        var t = Assert.IsType<AsrEvent.Transcript>(Assert.Single(events));
        Assert.Equal("料号确认", t.Text);
        Assert.Equal("Teilenummer bestätigt", t.Translations!["de"]);
    }

    [Fact]
    public void TranslationForUnknownUtteranceIsDropped()
    {
        var wire = new GladiaWire();

        var events = wire.Parse("""
            {"type":"translation","data":{"id":"nope","target_language":"de",
             "translated_utterance":{"text":"x"}}}
            """).ToList();

        Assert.Empty(events);
    }

    [Theory]
    [InlineData("not json at all")]
    [InlineData("{\"no_type\":true}")]
    [InlineData("{\"type\":\"speech_start\"}")]
    [InlineData("{\"type\":\"transcript\",\"data\":{}}")]
    public void UnknownOrMalformedMessagesAreIgnored(string json)
    {
        Assert.Empty(new GladiaWire().Parse(json));
    }
}
