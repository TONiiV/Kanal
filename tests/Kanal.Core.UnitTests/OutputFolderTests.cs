using Kanal.Host.Services;

namespace Kanal.Core.UnitTests;

/// <summary>
/// Where an exported transcript is offered. A meeting's own transcript and recording are no
/// longer settings at all — they live in the meeting record, beside each other.
/// </summary>
public class OutputFolderTests
{
    [Fact]
    public void AnUnsetFolderFallsBackToOneObviousPlace()
    {
        var transcripts = SettingsStore.ResolveTranscriptFolder(new AppSettings());

        Assert.EndsWith("Kanal", transcripts);
        Assert.Contains(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), transcripts);
    }

    [Fact]
    public void TheConfiguredFolderWins()
    {
        var settings = new AppSettings { TranscriptFolder = @"D:\meetings\transcripts" };

        Assert.Equal(@"D:\meetings\transcripts", SettingsStore.ResolveTranscriptFolder(settings));
    }

    /// <summary>Blank is not a folder — a cleared text box must fall back, not write to "".</summary>
    [Fact]
    public void BlankIsTreatedAsUnset()
    {
        Assert.EndsWith("Kanal", SettingsStore.ResolveTranscriptFolder(
            new AppSettings { TranscriptFolder = "   " }));
    }
}
