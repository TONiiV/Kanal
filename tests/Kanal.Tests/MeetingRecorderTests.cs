using Kanal.Audio;
using Kanal.Host.Services;

namespace Kanal.Tests;

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

    [Fact]
    public void ThePathIsTheWritersPath()
    {
        var path = TempFile();
        using var recorder = new MeetingRecorder(new WavWriter(path), _ => { });

        Assert.Equal(path, recorder.Path);
    }
}
