using Kanal.Core.Providers;
using Kanal.Providers.Gladia;

namespace Kanal.Core.UnitTests;

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
    public void RealWireTranslationMapsUtteranceIdAndChannelToTranscriptId()
    {
        // exact shape observed live 2026-07-30: translation carries utterance_id "3",
        // while the transcript id is "00_00000003" (channel_sequence)
        var wire = new GladiaWire();
        wire.Parse("""
            {"type":"transcript","data":{"id":"00_00000003","is_final":true,
             "utterance":{"text":"料号确认","language":"zh","start":0,"end":1.0,"channel":0}}}
            """).ToList();

        var events = wire.Parse("""
            {"type":"translation","data":{"utterance_id":"3",
             "utterance":{"text":"料号确认","language":"zh","start":0,"end":1.0,"channel":0},
             "original_language":"zh","target_language":"de",
             "translated_utterance":{"text":"Teilenummer bestätigt","language":"de","channel":0}}}
            """).ToList();

        var t = Assert.IsType<AsrEvent.Transcript>(Assert.Single(events));
        Assert.Equal("00_00000003", t.UtteranceId);
        Assert.Equal("Teilenummer bestätigt", t.Translations!["de"]);
    }

    [Fact]
    public void SourceLanguageSelfTranslationIsDropped()
    {
        // Gladia "translates" zh→zh with garbage output when the source is in target_languages
        var wire = new GladiaWire();
        wire.Parse("""
            {"type":"transcript","data":{"id":"00_00000000","is_final":true,
             "utterance":{"text":"料号确认","language":"zh","start":0,"end":1.0,"channel":0}}}
            """).ToList();

        var events = wire.Parse("""
            {"type":"translation","data":{"utterance_id":"0",
             "utterance":{"text":"料号确认","language":"zh","channel":0},
             "original_language":"zh","target_language":"zh",
             "translated_utterance":{"text":"料 号 确 认 确认。","language":"zh","channel":0}}}
            """).ToList();

        Assert.Empty(events);
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

    /// <summary>
    /// An error frame carries back the request that caused it, and that request contains what was
    /// said in the room. The message this produces is shown on screen and written to the log file,
    /// so falling back to the raw frame put an utterance in both.
    /// </summary>
    [Fact]
    public void AnErrorFrameWithNoMessageIsDescribedRatherThanQuoted()
    {
        var wire = new GladiaWire();

        var events = wire.Parse("""
            {"type":"error","data":{"code":422,"request":{"type":"transcript","data":
             {"utterance":{"text":"KX-4402 的公差是正负 0.05 毫米，11 月 15 日交货"}}}}}
            """).ToList();

        var error = Assert.IsType<AsrEvent.Error>(Assert.Single(events));
        Assert.DoesNotContain("KX-4402", error.Message);
        Assert.DoesNotContain("0.05", error.Message);
        Assert.Contains("422", error.Message);
        Assert.False(error.Fatal);
    }

    /// <summary>A frame that does say what went wrong is quoted as-is: that is the diagnostic.</summary>
    [Fact]
    public void AnErrorFrameWithAMessageKeepsIt()
    {
        var wire = new GladiaWire();

        var events = wire.Parse("""{"type":"error","data":{"code":429,"message":"rate limit exceeded"}}""")
            .ToList();

        Assert.Equal("rate limit exceeded", Assert.IsType<AsrEvent.Error>(Assert.Single(events)).Message);
    }
}
