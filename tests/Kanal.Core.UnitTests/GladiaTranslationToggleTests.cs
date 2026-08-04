using Kanal.Core.Providers;
using Kanal.Providers.Gladia;

namespace Kanal.Core.UnitTests;

public class GladiaTranslationToggleTests
{
    private static GladiaOptions Options(bool enable) =>
        new() { ApiKey = "test", EnableTranslation = enable };

    private static readonly AsrSessionOptions Session =
        new(16_000, ["zh", "de", "pl"], ["zh", "de", "pl"]);

    [Fact]
    public void TranslationIsOnByDefault()
    {
        Assert.True(new GladiaOptions { ApiKey = "test" }.EnableTranslation);
    }

    [Fact]
    public void CapsFollowTheToggle()
    {
        using var on = new GladiaAsrProvider(Options(enable: true));
        using var off = new GladiaAsrProvider(Options(enable: false));
        Assert.True(on.Caps.Translation);
        Assert.False(off.Caps.Translation);
    }

    [Fact]
    public void InitBodyCarriesTranslationConfigWhenEnabled()
    {
        var body = GladiaAsrProvider.BuildInitBody(Options(enable: true), Session);

        var processing = Assert.IsAssignableFrom<System.Text.Json.Nodes.JsonObject>(
            body["realtime_processing"]);
        Assert.Equal(true, (bool?)processing["translation"]);
        Assert.NotNull(processing["translation_config"]);
    }

    [Fact]
    public void InitBodyOmitsTranslationConfigWhenDisabled()
    {
        var body = GladiaAsrProvider.BuildInitBody(Options(enable: false), Session);

        Assert.False(body.ContainsKey("realtime_processing"));
        // transcription config itself is untouched
        Assert.NotNull(body["language_config"]);
        Assert.NotNull(body["messages_config"]);
    }
}
