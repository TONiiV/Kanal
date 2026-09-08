using Kanal.Core.Models;
using Kanal.Core.Workspaces;

namespace Kanal.Core.UnitTests;

/// <summary>
/// The transcript is written as the meeting runs, for the same reason the recording is: an hour
/// held in memory means one crash costs all of it. A host that dies mid-meeting must not leave a
/// playable recording next to an empty transcript.
/// </summary>
public class TranscriptLogTests
{
    private static string TempFile() => Path.Combine(
        Path.GetTempPath(), "kanal-log-" + Guid.NewGuid().ToString("N"), TranscriptLog.FileName);

    private static Utterance Said(
        string id,
        string text,
        int revision = 1,
        IReadOnlyDictionary<string, string>? translations = null) =>
        new(id, "S1", 1000, 2400, "zh", text, revision, UtteranceState.Final,
            CodeSwitch: false, SpeakerConfidence: 0.9,
            translations ?? new Dictionary<string, string>());

    [Fact]
    public void TheTranscriptIsReadableWhileTheMeetingIsStillRunning()
    {
        var path = TempFile();
        using var writer = new TranscriptLogWriter(path, _ => { });

        writer.Append(Said("u1", "KX-4402 的公差"));
        writer.Append(Said("u2", "Zweite Zeile"));

        var read = TranscriptLog.Read(path);
        Assert.Equal(["KX-4402 的公差", "Zweite Zeile"], read.Select(u => u.SrcText));
    }

    [Fact]
    public void EverythingNeededToRebuildAnUtteranceSurvivesTheRoundTrip()
    {
        var path = TempFile();
        var spoken = new Utterance(
            "u7", "S3", 12_500, 15_250, "pl", "wsporników", 4, UtteranceState.Final,
            CodeSwitch: true, SpeakerConfidence: 0.42,
            new Dictionary<string, string> { ["de"] = "Halterungen", ["zh"] = "支架" });

        using (var writer = new TranscriptLogWriter(path, _ => { }))
            writer.Append(spoken);

        // Field by field: a record holding a dictionary compares that field by reference.
        var read = Assert.Single(TranscriptLog.Read(path));
        Assert.Equal(spoken with { Translations = read.Translations }, read);
        Assert.Equal(spoken.Translations, read.Translations);
    }

    /// <summary>
    /// Translations land after the final they belong to, so the same id is written more than
    /// once. The last line for an id wins; the order is the order the ids were first heard in.
    /// </summary>
    [Fact]
    public void ALaterRevisionSupersedesTheLineBeforeIt()
    {
        var path = TempFile();
        using (var writer = new TranscriptLogWriter(path, _ => { }))
        {
            writer.Append(Said("u1", "erste"));
            writer.Append(Said("u2", "zweite"));
            writer.Append(Said("u1", "erste", revision: 2,
                translations: new Dictionary<string, string> { ["zh"] = "第一句" }));
        }

        var read = TranscriptLog.Read(path);
        Assert.Equal(["u1", "u2"], read.Select(u => u.Id));
        Assert.Equal(2, read[0].Revision);
        Assert.Equal("第一句", read[0].Translations["zh"]);
    }

    /// <summary>
    /// The host being killed mid-write, as the file sees it: the last line stops in the middle of
    /// itself. Every completed line before it is still a sentence somebody said.
    /// </summary>
    [Fact]
    public void AHalfWrittenLastLineDoesNotCostTheLinesBeforeIt()
    {
        var path = TempFile();
        using (var writer = new TranscriptLogWriter(path, _ => { }))
        {
            writer.Append(Said("u1", "erste"));
            writer.Append(Said("u2", "zweite"));
            writer.Append(Said("u3", "dritte"));
        }

        var text = File.ReadAllText(path);
        File.WriteAllText(path, text[..(text.LastIndexOf("u3", StringComparison.Ordinal) + 8)]);

        Assert.Equal(["u1", "u2"], TranscriptLog.Read(path).Select(u => u.Id));
    }

    /// <summary>
    /// The same policy the recording has: the log sits on the meeting's own path, so a failure
    /// costs the transcript, once, reported once — never the meeting.
    /// </summary>
    [Fact]
    public void AnAppendThatFailsStopsTheLogAndReportsOnce()
    {
        var sink = new StringWriter();
        sink.Dispose(); // the disk going away, as seen by the next append
        var reasons = new List<string>();
        var writer = new TranscriptLogWriter(sink, "nowhere.jsonl", reasons.Add);

        writer.Append(Said("u1", "erste"));
        writer.Append(Said("u2", "zweite"));

        Assert.Single(reasons);
        Assert.NotEmpty(reasons[0]);
    }

    [Fact]
    public void AnAppendAfterDisposeIsDroppedWithoutAReport()
    {
        var stopped = 0;
        var writer = new TranscriptLogWriter(TempFile(), _ => stopped++);
        writer.Append(Said("u1", "erste"));

        writer.Dispose();
        writer.Append(Said("u2", "zweite"));

        Assert.Equal(0, stopped);
    }

    [Fact]
    public void AMeetingThatWroteNothingReadsAsEmptyRatherThanFailing()
    {
        Assert.Empty(TranscriptLog.Read(TempFile()));
    }
}
