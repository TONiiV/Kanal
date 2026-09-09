using System.Text.Json.Nodes;
using Kanal.Core.Providers;
using Kanal.Providers.Gladia;

namespace Kanal.Core.UnitTests;

public class GladiaLanguageDetectionTests
{
    [Fact]
    public void RecognitionIsNotLimitedToTheLanguagesTheOperatorPickedColumnsFor()
    {
        var body = GladiaAsrProvider.BuildInitBody(
            new GladiaOptions { ApiKey = "test" },
            new AsrSessionOptions(16_000, ["zh", "de", "pl"]));

        var language = Assert.IsAssignableFrom<JsonObject>(body["language_config"]);
        Assert.Equal(true, (bool?)language["code_switching"]);
        Assert.False(language.ContainsKey("languages"));
    }

    [Fact]
    public void TranslationStillTargetsExactlyTheColumnsOnScreen()
    {
        var body = GladiaAsrProvider.BuildInitBody(
            new GladiaOptions { ApiKey = "test", EnableTranslation = true },
            new AsrSessionOptions(16_000, ["zh", "de", "pl"]));

        var config = Assert.IsAssignableFrom<JsonObject>(
            Assert.IsAssignableFrom<JsonObject>(body["realtime_processing"])["translation_config"]);
        Assert.Equal(
            ["zh", "de", "pl"],
            Assert.IsAssignableFrom<JsonArray>(config["target_languages"]).Select(n => (string?)n));
    }
}
