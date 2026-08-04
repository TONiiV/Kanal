using Kanal.Host.Services;

namespace Kanal.Core.UnitTests;

/// <summary>Where the two artefacts of a meeting are written, and how that survives a restart.</summary>
public class OutputFolderTests
{
    [Fact]
    public void UnsetFoldersFallBackToOneObviousPlace()
    {
        var settings = new AppSettings();

        var transcripts = SettingsStore.ResolveTranscriptFolder(settings);
        var audio = SettingsStore.ResolveAudioFolder(settings);

        Assert.EndsWith("Kanal", transcripts);
        Assert.EndsWith("Kanal", audio);
        Assert.Contains(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), transcripts);
    }

    [Fact]
    public void ConfiguredFoldersWin()
    {
        var settings = new AppSettings
        {
            TranscriptFolder = @"D:\meetings\transcripts",
            AudioFolder = @"E:\meetings\audio",
        };

        Assert.Equal(@"D:\meetings\transcripts", SettingsStore.ResolveTranscriptFolder(settings));
        Assert.Equal(@"E:\meetings\audio", SettingsStore.ResolveAudioFolder(settings));
    }

    /// <summary>Blank is not a folder — a cleared text box must fall back, not write to "".</summary>
    [Fact]
    public void BlankIsTreatedAsUnset()
    {
        var settings = new AppSettings { TranscriptFolder = "   ", AudioFolder = "" };

        Assert.EndsWith("Kanal", SettingsStore.ResolveTranscriptFolder(settings));
        Assert.EndsWith("Kanal", SettingsStore.ResolveAudioFolder(settings));
    }
}
