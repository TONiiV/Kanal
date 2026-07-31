using Kanal.Audio;

namespace Kanal.Tests;

/// <summary>
/// The recording of a meeting is written as it happens, not assembled at the end: an hour of
/// 16 kHz mono is about 115 MB, and holding that to write once on Stop means a crash costs the
/// whole thing.
/// </summary>
public class WavWriterTests
{
    private static string TempFile() => Path.Combine(
        Path.GetTempPath(), "kanal-wav-" + Guid.NewGuid().ToString("N") + ".wav");

    private static byte[] Tone(int samples)
    {
        var pcm = new byte[samples * 2];
        for (var i = 0; i < samples; i++)
        {
            var value = (short)(short.MaxValue / 2 * Math.Sin(i * 0.05));
            pcm[i * 2] = (byte)(value & 0xFF);
            pcm[i * 2 + 1] = (byte)((value >> 8) & 0xFF);
        }

        return pcm;
    }

    [Fact]
    public void WritesAFileTheReaderAgreesWith()
    {
        var path = TempFile();
        var frames = Enumerable.Range(0, 20).Select(_ => Tone(160)).ToList();

        using (var writer = new WavWriter(path))
        {
            foreach (var frame in frames)
                writer.Write(frame);
        }

        using var stream = File.OpenRead(path);
        var read = WavFile.Read(stream);

        Assert.Equal(16_000, read.SampleRateHz);
        Assert.Equal(1, read.Channels);
        Assert.Equal(frames.Sum(f => f.Length), read.Pcm16.Length);
        Assert.Equal(frames.SelectMany(f => f).ToArray(), read.Pcm16);
    }

    [Fact]
    public void ReportsHowMuchWasRecorded()
    {
        var path = TempFile();
        using var writer = new WavWriter(path);

        writer.Write(Tone(16_000)); // exactly one second at 16 kHz mono

        Assert.Equal(32_000, writer.DataBytes);
        Assert.Equal(1.0, writer.Duration.TotalSeconds, 3);
    }

    /// <summary>
    /// The point of patching lengths as it goes. A host that dies mid-meeting must leave a file
    /// that still plays — a WAV whose lengths are zero is not a truncated recording, it is one
    /// most players refuse to open at all.
    /// </summary>
    [Fact]
    public void AFileStillBeingWrittenIsAlreadyPlayable()
    {
        var path = TempFile();
        using var writer = new WavWriter(path);

        // past the flush threshold, so the lengths in the header have been patched at least once
        for (var i = 0; i < 15; i++)
            writer.Write(Tone(8_000));

        using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var read = WavFile.Read(stream);

        Assert.Equal(16_000, read.SampleRateHz);
        Assert.True(read.Pcm16.Length > 0, "a reader saw a zero-length recording mid-meeting.");
        Assert.True(
            read.Pcm16.Length <= writer.DataBytes,
            "the header claimed more audio than had been written.");
    }

    [Fact]
    public void CreatesTheFolderItWasPointedAt()
    {
        var dir = Path.Combine(Path.GetTempPath(), "kanal-wav-" + Guid.NewGuid().ToString("N"), "nested");
        var path = Path.Combine(dir, "room.wav");

        using (var writer = new WavWriter(path))
            writer.Write(Tone(160));

        Assert.True(File.Exists(path));
    }

    [Fact]
    public void DisposeIsIdempotent()
    {
        var writer = new WavWriter(TempFile());
        writer.Write(Tone(160));

        writer.Dispose();
        writer.Dispose();

        Assert.Throws<ObjectDisposedException>(() => writer.Write(Tone(160)));
    }
}
