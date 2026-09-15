using System.Text.Json;
using Kanal.Host.Services;

namespace Kanal.Core.UnitTests;

public class AppSettingsTests
{
    [Fact]
    public void ActiveTranslationModelIdRoundTrips()
    {
        var settings = new AppSettings { ActiveTranslationModelId = "qwen3.5-4b" };

        var json = JsonSerializer.Serialize(settings);
        var loaded = JsonSerializer.Deserialize<AppSettings>(json)!;

        Assert.Equal("qwen3.5-4b", loaded.ActiveTranslationModelId);
    }

    [Fact]
    public void ActiveTranscriptionModelIdRoundTrips()
    {
        var settings = new AppSettings { ActiveTranscriptionModelId = "nemotron-3.5-asr-560ms-int8" };

        var json = JsonSerializer.Serialize(settings);
        var loaded = JsonSerializer.Deserialize<AppSettings>(json)!;

        Assert.Equal("nemotron-3.5-asr-560ms-int8", loaded.ActiveTranscriptionModelId);
    }

    [Fact]
    public void LegacySettingsFileLoadsWithCloudDefault()
    {
        // settings written before this feature carry no model field — null means Gladia cloud
        var loaded = JsonSerializer.Deserialize<AppSettings>(
            """{"ApiKeys":[],"ActiveGladiaKeyName":null}""")!;

        Assert.Null(loaded.ActiveTranslationModelId);
        Assert.Null(loaded.ActiveTranscriptionModelId);
    }

    [Fact]
    public void ModelsPathLivesUnderTheKanalAppDataDirectory()
    {
        Assert.EndsWith(Path.Combine("Kanal", "models"), SettingsStore.ModelsPath);
    }
}
