using Kanal.Audio;
using Kanal.Core.Meetings;
using Kanal.Host.Services;

namespace Kanal.Core.UnitTests;

/// <summary>
/// The failure policy around the recording. The recorder sits on the audio capture path: an
/// exception escaping it does not cost the recording, it costs the meeting — the capture loop
/// dies with it. A full disk or a pulled USB stick must stop the recording, once, reported
/// once, and nothing else.
/// </summary>
public class MeetingRecorderTests
{
    private static string TempFile() => Path.Combine(
        Path.GetTempPath(), "kanal-rec-" + Guid.NewGuid().ToString("N") + ".wav");

    [Fact]
    public void AWorkingRecorderWritesAndNeverReports()
    {
        var path = TempFile();
        var stopped = 0;
        using (var recorder = new MeetingRecorder(new WavWriter(path), _ => stopped++))
        {
            recorder.Write(new byte[320]);
            recorder.Write(new byte[320]);
        }

        Assert.Equal(0, stopped);
        using var stream = File.OpenRead(path);
        Assert.Equal(640, WavFile.Read(stream).Pcm16.Length);
    }

    /// <summary>
    /// The first write that fails ends the recording and says why — exactly once. Every later
    /// frame is dropped silently: without the guard, each one would hit the disposed writer,
    /// throw again, and overwrite the real reason ("disk full") with "cannot access a disposed
    /// object", forever.
    /// </summary>
    [Fact]
    public void AFailedWriteStopsTheRecordingAndReportsOnce()
    {
        var writer = new WavWriter(TempFile());
        var reasons = new List<string>();
        var recorder = new MeetingRecorder(writer, reasons.Add);

        writer.Dispose(); // the disk going away, as seen by the next write

        recorder.Write(new byte[320]);
        recorder.Write(new byte[320]);
        recorder.Write(new byte[320]);

        Assert.Single(reasons);
        Assert.NotEmpty(reasons[0]);
    }

    /// <summary>
    /// A frame that arrives after Stop is not a failure — the meeting is being torn down and a
    /// straggler must not put "Recording stopped" over the "Stopped. Audio saved to…" message.
    /// </summary>
    [Fact]
    public void AFrameAfterDisposeIsDroppedWithoutAReport()
    {
        var stopped = 0;
        var recorder = new MeetingRecorder(new WavWriter(TempFile()), _ => stopped++);
        recorder.Write(new byte[320]);

        recorder.Dispose();
        recorder.Write(new byte[320]);

        Assert.Equal(0, stopped);
    }

    /// <summary>
    /// The recorder is the only thing that knows which byte of the meeting the file starts at,
    /// so it is the thing that tells the timeline — rather than a second copy of that arithmetic
    /// at the call site, which would be right only as long as both are edited together.
    /// </summary>
    [Fact]
    public void TheTimelineLearnsWhichStretchOfTheMeetingTheFileHolds()
    {
        var timeline = new MeetingTimeline();
        var path = TempFile();
        timeline.Append(new byte[32_000]);

        using (new MeetingRecorder(new WavWriter(path), _ => { }, timeline))
        {
            timeline.Append(new byte[32_000]);
            timeline.Observe("u1", 1_000, 2_000);
        }

        timeline.Append(new byte[32_000]);
        timeline.Observe("after", 2_000, 3_000);

        var during = timeline.LocateInRecording("u1");
        Assert.Equal(AudioAvailability.Available, during.Availability);
        Assert.Equal(path, during.Path);
        Assert.Equal(0, during.StartDataByte);
        Assert.Equal(32_000, during.EndDataByte);
        Assert.Equal(AudioAvailability.NotRecorded, timeline.LocateInRecording("after").Availability);
    }

    [Fact]
    public void AFailedWriteClosesTheRecordingOnTheTimelineToo()
    {
        var timeline = new MeetingTimeline();
        var writer = new WavWriter(TempFile());
        var recorder = new MeetingRecorder(writer, _ => { }, timeline);
        writer.Dispose();

        recorder.Write(new byte[320]);

        timeline.Append(new byte[32_000]);
        timeline.Observe("after", 0, 1_000);
        Assert.Equal(AudioAvailability.NotRecorded, timeline.LocateInRecording("after").Availability);
    }

    [Fact]
    public void ThePathIsTheWritersPath()
    {
        var path = TempFile();
        using var recorder = new MeetingRecorder(new WavWriter(path), _ => { });

        Assert.Equal(path, recorder.Path);
    }
}
