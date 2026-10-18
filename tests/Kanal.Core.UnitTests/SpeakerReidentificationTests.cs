using Kanal.Core.Meetings;
using Kanal.Core.Models;
using Kanal.Core.Workspaces;
using Kanal.Providers.LocalDiarization;

namespace Kanal.Core.UnitTests;

public class SpeakerReidentificationTests
{
    private static Utterance Said(string id, long startMs, long? endMs, string tag = "S01") =>
        new(id, tag, startMs, endMs, "de", $"text {id}", 1, UtteranceState.Final, false, 1,
            new Dictionary<string, string>());

    private static string NewFolder()
    {
        var folder = Path.Combine(Path.GetTempPath(), "kanal-speakers", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        return folder;
    }

    private sealed class FakeDiarizer(params SpeakerTurn[] turns) : ISpeakerDiarizer
    {
        public int? AskedFor { get; private set; } = -1;

        public Task<IReadOnlyList<SpeakerTurn>> DiarizeAsync(
            string wavPath, int? speakerCount, IProgress<double>? progress, CancellationToken ct)
        {
            AskedFor = speakerCount;
            progress?.Report(1);
            return Task.FromResult<IReadOnlyList<SpeakerTurn>>(turns);
        }
    }

    [Fact]
    public void EachUtteranceTakesTheSpeakerWithTheLargestOverlap()
    {
        var tags = SpeakerReidentification.Assign(
            [Said("u1", 0, 4_000), Said("u2", 4_000, 10_000)],
            [new(0, 3.0, 7), new(3.0, 5.0, 2), new(5.0, 10.0, 7)]);

        Assert.Equal("D01", tags["u1"]);
        Assert.Equal("D01", tags["u2"]);
    }

    [Fact]
    public void OverlapIsSummedOverEveryTurnOfOneSpeaker()
    {
        var tags = SpeakerReidentification.Assign(
            [Said("u1", 0, 10_000)],
            [new(0, 2.0, 1), new(2.0, 6.0, 2), new(6.0, 9.0, 1)]);

        Assert.Equal("D01", tags["u1"]);
        Assert.Single(tags);
    }

    [Fact]
    public void TagsAreNumberedInTheOrderPeopleFirstSpeak()
    {
        var tags = SpeakerReidentification.Assign(
            [Said("late", 20_000, 22_000), Said("early", 0, 2_000)],
            [new(0, 2.0, 5), new(20.0, 22.0, 0)]);

        Assert.Equal("D01", tags["early"]);
        Assert.Equal("D02", tags["late"]);
    }

    [Fact]
    public void AnUtteranceNoTurnCoversKeepsItsOriginalTag()
    {
        var utterances = new[] { Said("covered", 0, 2_000), Said("silent", 30_000, 31_000), Said("open", 0, null) };
        var tags = SpeakerReidentification.Assign(utterances, [new(0, 2.0, 0)]);

        Assert.Equal(["covered"], tags.Keys);
        var shown = SpeakerReidentification.Apply(utterances, tags);
        Assert.Equal(["D01", "S01", "S01"], shown.Select(u => u.SpeakerTag));
    }

    [Fact]
    public async Task RunWritesASidecarAndLeavesTheTranscriptUntouched()
    {
        var folder = NewFolder();
        var transcript = Path.Combine(folder, TranscriptLog.FileName);
        using (var log = new TranscriptLogWriter(transcript, _ => { }))
        {
            log.Append(Said("u1", 0, 2_000));
            log.Append(Said("u2", 2_000, 4_000));
        }

        var before = File.ReadAllBytes(transcript);
        var diarizer = new FakeDiarizer(new(0, 2.0, 3), new(2.0, 4.0, 1));

        var speakers = await SpeakerReidentification.RunAsync(
            diarizer, transcript, Path.Combine(folder, "audio.wav"), null, null, TestContext.Current.CancellationToken);

        Assert.Equal(2, speakers);
        Assert.Null(diarizer.AskedFor);
        Assert.Equal(before, File.ReadAllBytes(transcript));
        Assert.True(File.Exists(Path.Combine(folder, SpeakerReidentification.FileName)));

        var original = TranscriptLog.Read(transcript);
        Assert.All(original, u => Assert.Equal("S01", u.SpeakerTag));
        var shown = SpeakerReidentification.Apply(original, SpeakerReidentification.Read(folder));
        Assert.Equal(["D01", "D02"], shown.Select(u => u.SpeakerTag));
    }

    [Fact]
    public async Task TheOperatorsSpeakerCountReachesTheDiarizer()
    {
        var folder = NewFolder();
        var transcript = Path.Combine(folder, TranscriptLog.FileName);
        var diarizer = new FakeDiarizer();

        await SpeakerReidentification.RunAsync(diarizer, transcript, "audio.wav", 4, null, TestContext.Current.CancellationToken);

        Assert.Equal(4, diarizer.AskedFor);
    }

    [Fact]
    public void DeletingTheSidecarRestoresTheOriginalTags()
    {
        var folder = NewFolder();
        SpeakerReidentification.Write(folder, new Dictionary<string, string> { ["u1"] = "D01" });
        File.Delete(Path.Combine(folder, SpeakerReidentification.FileName));

        Assert.Empty(SpeakerReidentification.Read(folder));
    }

    [Fact]
    public void AnUnreadableSidecarShowsTheOriginalTags()
    {
        var folder = NewFolder();
        File.WriteAllText(Path.Combine(folder, SpeakerReidentification.FileName), "{ not json");

        Assert.Empty(SpeakerReidentification.Read(folder));
    }

    [Fact]
    public async Task AMissingModelFailsBeforeTheNativeLibraryIsCalled()
    {
        var folder = NewFolder();
        var diarizer = new SherpaSpeakerDiarizer(
            Path.Combine(folder, "missing-segmentation.onnx"), Path.Combine(folder, "missing-embedding.onnx"));

        await Assert.ThrowsAsync<FileNotFoundException>(() => diarizer.DiarizeAsync(
            Path.Combine(folder, "audio.wav"), null, null, TestContext.Current.CancellationToken));
    }

    [Fact]
    public void ANonAsciiModelPathIsRefusedOnWindowsOnly()
    {
        var folder = Path.Combine(NewFolder(), "Łukasz");
        Directory.CreateDirectory(folder);
        var model = Path.Combine(folder, "model.onnx");
        File.WriteAllBytes(model, [0]);

        Assert.Throws<IOException>(() => SherpaSpeakerDiarizer.CheckModelPath(model, isWindows: true));
        SherpaSpeakerDiarizer.CheckModelPath(model, isWindows: false);
    }

    private sealed record TestFile(string FileName, string DownloadUrl, long SizeBytes, string Sha256)
        : IDownloadableFile;

    [Fact]
    public async Task SherpaSeparatesFourSpeakersInARealRecording()
    {
        var folder = Environment.GetEnvironmentVariable("KANAL_DIARIZATION_MODELS");
        Assert.SkipWhen(string.IsNullOrEmpty(folder),
            "Set KANAL_DIARIZATION_MODELS to a folder; the default models and a test recording download into it.");

        var downloads = new ModelDownloadManager(folder!);
        var recording = new TestFile(
            "0-four-speakers-zh.wav",
            "https://github.com/k2-fsa/sherpa-onnx/releases/download/speaker-segmentation-models/0-four-speakers-zh.wav",
            1_819_586,
            "bedf036caed208386c67b4ef4b11f83d74dd0d420b102163a1c33cd09cde7010");
        IDownloadableFile[] files =
            [DiarizationModelCatalog.DefaultSegmentation, DiarizationModelCatalog.DefaultEmbedding, recording];
        foreach (var file in files.Where(f => !downloads.IsDownloaded(f)))
            await downloads.DownloadAsync(file, null, TestContext.Current.CancellationToken);

        var reported = new List<double>();
        var turns = await new SherpaSpeakerDiarizer(
                downloads.GetPath(DiarizationModelCatalog.DefaultSegmentation),
                downloads.GetPath(DiarizationModelCatalog.DefaultEmbedding))
            .DiarizeAsync(downloads.GetPath(recording), 4, new SyncProgress(reported.Add),
                TestContext.Current.CancellationToken);

        Assert.Equal(4, turns.Select(t => t.Speaker).Distinct().Count());
        Assert.All(turns, t => Assert.True(t.EndSeconds > t.StartSeconds));
        Assert.Equal(1.0, reported[^1], 3);
    }

    [Fact]
    public void WithoutACountTheClusteringMergesAtTheMeasuredThreshold()
    {
        var clustering = SherpaSpeakerDiarizer.Clustering(null);

        Assert.Equal(0.8f, clustering.Threshold);
        Assert.True(clustering.NumClusters <= 0);
    }

    [Fact]
    public void AGivenCountFixesTheNumberOfClusters()
    {
        Assert.Equal(3, SherpaSpeakerDiarizer.Clustering(3).NumClusters);
    }

    private sealed class SyncProgress(Action<double> report) : IProgress<double>
    {
        public void Report(double value) => report(value);
    }
}
