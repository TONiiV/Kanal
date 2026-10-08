using Kanal.Core.Providers;
using Kanal.Providers.LocalAsr;

namespace Kanal.Core.UnitTests;

public class NemotronAsrProviderTests
{
    private sealed class ScriptedStream(params (string Text, bool Endpoint)[] steps) : IRecognizerStream
    {
        private int _step = -1;

        public List<float[]> Accepted { get; } = [];
        public int Resets { get; private set; }
        public bool Finished { get; private set; }
        public bool Disposed { get; private set; }
        public string FinishText { get; init; } = "";
        public Exception? Throw { get; init; }

        private (string Text, bool Endpoint) Current => _step < steps.Length ? steps[_step] : ("", false);

        public void Accept(float[] samples)
        {
            Accepted.Add(samples);
            _step++;
        }

        public string Decode() => Throw is not null ? throw Throw : Finished ? FinishText : Current.Text;

        public bool IsEndpoint => !Finished && Current.Endpoint;

        public void Reset() => Resets++;

        public void Finish() => Finished = true;

        public void Dispose() => Disposed = true;
    }

    private static readonly string[] Room = ["zh", "de", "pl"];

    // 100 ms of 16 kHz PCM16
    private static readonly byte[] Chunk = new byte[3200];

    private static async Task<List<AsrEvent>> RunAsync(
        ScriptedStream stream, int chunks, IReadOnlyList<string>? room = null)
    {
        var provider = new NemotronAsrProvider(_ => stream);
        var session = await provider.StartAsync(new AsrSessionOptions(16_000, room ?? Room), default);
        for (var i = 0; i < chunks; i++)
            await session.PushAudioAsync(Chunk);
        await session.DisposeAsync();

        var events = new List<AsrEvent>();
        await foreach (var e in session.Events)
            events.Add(e);
        return events;
    }

    [Fact]
    public async Task PartialsGrowUntilAnEndpointMakesThemFinal()
    {
        var stream = new ScriptedStream(
            ("Die", false),
            ("Die Toleranzen", false),
            ("Die Toleranzen", false),
            ("Die Toleranzen sind frei", true),
            ("", false),
            ("Czy", false)) { FinishText = "Czy próbki" };

        var events = await RunAsync(stream, chunks: 6);

        var t = events.OfType<AsrEvent.Transcript>().ToList();
        Assert.Equal(
            ["Die", "Die Toleranzen", "Die Toleranzen sind frei", "Czy", "Czy próbki"],
            t.Select(e => e.Text));
        Assert.Equal([false, false, true, false, true], t.Select(e => e.IsFinal));
        Assert.Equal(["de", "de", "de", "pl", "pl"], t.Select(e => e.SrcLang));

        Assert.Single(t.Take(3).Select(e => e.UtteranceId).Distinct());
        Assert.NotEqual(t[0].UtteranceId, t[3].UtteranceId);
        Assert.Equal(t[3].UtteranceId, t[4].UtteranceId);

        Assert.Equal((0L, (long?)400), (t[2].TStartMs, t[2].TEndMs));
        Assert.Null(t[0].TEndMs);
        Assert.Equal((400L, (long?)600), (t[4].TStartMs, t[4].TEndMs));
        Assert.All(t, e => Assert.Null(e.Translations));

        Assert.IsType<AsrEvent.Ended>(events[^1]);
        Assert.Equal(1, stream.Resets);
        Assert.True(stream.Finished);
        Assert.True(stream.Disposed);
    }

    [Fact]
    public async Task AnEndpointOverSilenceEmitsNothing()
    {
        var stream = new ScriptedStream(("", true), ("  ", true));

        var events = await RunAsync(stream, chunks: 2);

        Assert.Empty(events.OfType<AsrEvent.Transcript>());
        Assert.Equal(2, stream.Resets);
    }

    [Fact]
    public async Task AudioArrivesAsFloatSamples()
    {
        var stream = new ScriptedStream();
        var provider = new NemotronAsrProvider(_ => stream);
        var session = await provider.StartAsync(new AsrSessionOptions(16_000, Room), default);

        await session.PushAudioAsync(new byte[] { 0x00, 0x40, 0x00, 0xC0 });
        await session.DisposeAsync();

        Assert.Equal([0.5f, -0.5f], Assert.Single(stream.Accepted));
    }

    [Theory]
    [InlineData(new[] { "zh" }, "zh-CN")]
    [InlineData(new[] { "ja" }, "ja-JP")]
    [InlineData(new[] { "pl" }, "pl")]
    [InlineData(new[] { "zh", "de", "pl" }, "auto")]
    public async Task ARoomOfOneLanguageForcesItAndAnyOtherRoomDetects(string[] room, string expected)
    {
        string? asked = null;
        var provider = new NemotronAsrProvider(language =>
        {
            asked = language;
            return new ScriptedStream();
        });

        await (await provider.StartAsync(new AsrSessionOptions(16_000, room), default)).DisposeAsync();

        Assert.Equal(expected, asked);
    }

    [Fact]
    public async Task AForcedLanguageIsAlsoTheSourceLanguage()
    {
        var events = await RunAsync(new ScriptedStream(("KX-4402", true)), chunks: 1, room: ["zh"]);

        Assert.Equal("zh", Assert.Single(events.OfType<AsrEvent.Transcript>()).SrcLang);
    }

    [Fact]
    public async Task ARecognizerFailureEndsTheSessionWithAFatalError()
    {
        var events = await RunAsync(
            new ScriptedStream(("x", false)) { Throw = new InvalidOperationException("boom") }, chunks: 1);

        var error = Assert.IsType<AsrEvent.Error>(Assert.Single(events));
        Assert.True(error.Fatal);
        Assert.Contains("boom", error.Message);
    }

    [Fact]
    public void ItTranscribesOnlyAndLeavesTranslationToTheMtProvider()
    {
        var caps = new NemotronAsrProvider(_ => new ScriptedStream()).Caps;

        Assert.False(caps.Translation);
        Assert.False(caps.Diarization);
        Assert.True(caps.Streaming);
        Assert.True(caps.AutoLanguageDetect);
        Assert.Superset(new HashSet<string> { "zh", "de", "pl", "en" }, caps.Languages.ToHashSet());
    }
}
