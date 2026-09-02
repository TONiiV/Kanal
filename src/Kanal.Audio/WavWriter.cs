using System.Buffers.Binary;
using System.Text;

namespace Kanal.Audio;

/// <summary>
/// Streams PCM16 frames into a WAV file as a meeting runs. Separate from <see cref="WavFile"/>,
/// which writes a buffer it already holds: an hour of 16 kHz mono is ~115 MB, and holding it in
/// memory to write once at the end means a crash costs the whole recording.
/// </summary>
/// <remarks>
/// The RIFF and data lengths are patched periodically rather than only on close, so a host that
/// dies mid-meeting still leaves a file that plays up to the last flush. A WAV whose lengths are
/// zero is not a truncated recording — it is one most players refuse to open at all.
/// </remarks>
public sealed class WavWriter : IDisposable
{
    /// <summary>Roughly two seconds of 16 kHz mono: the most a crash should cost.</summary>
    private const int FlushEvery = 64_000;

    private readonly FileStream _stream;
    private readonly int _sampleRateHz;
    private readonly int _channels;
    private long _dataBytes;
    private long _sinceFlush;
    private bool _disposed;

    public WavWriter(string path, int sampleRateHz = 16_000, int channels = 1)
    {
        Path = path;
        _sampleRateHz = sampleRateHz;
        _channels = channels;

        var directory = System.IO.Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        // FileShare.Read so the recording can be copied or played while it is still being made
        _stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);
        WriteHeader();
    }

    public string Path { get; }

    public long DataBytes => _dataBytes;

    public TimeSpan Duration =>
        TimeSpan.FromSeconds(_dataBytes / (double)(_sampleRateHz * _channels * 2));

    public void Write(ReadOnlySpan<byte> pcm16)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        _stream.Write(pcm16);
        _dataBytes += pcm16.Length;
        _sinceFlush += pcm16.Length;

        if (_sinceFlush < FlushEvery)
            return;

        _sinceFlush = 0;
        PatchLengths();
        _stream.Flush();
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        try
        {
            PatchLengths();
            _stream.Flush();
        }
        catch (IOException)
        {
            // a full or disconnected disk: the frames already written are still worth keeping
        }
        finally
        {
            _stream.Dispose();
        }
    }

    private void WriteHeader()
    {
        var byteRate = _sampleRateHz * _channels * 2;
        Span<byte> header = stackalloc byte[44];
        Encoding.ASCII.GetBytes("RIFF", header[..4]);
        BinaryPrimitives.WriteInt32LittleEndian(header[4..8], 36); // patched as data arrives
        Encoding.ASCII.GetBytes("WAVE", header[8..12]);
        Encoding.ASCII.GetBytes("fmt ", header[12..16]);
        BinaryPrimitives.WriteInt32LittleEndian(header[16..20], 16);
        BinaryPrimitives.WriteInt16LittleEndian(header[20..22], 1); // PCM
        BinaryPrimitives.WriteInt16LittleEndian(header[22..24], (short)_channels);
        BinaryPrimitives.WriteInt32LittleEndian(header[24..28], _sampleRateHz);
        BinaryPrimitives.WriteInt32LittleEndian(header[28..32], byteRate);
        BinaryPrimitives.WriteInt16LittleEndian(header[32..34], (short)(_channels * 2));
        BinaryPrimitives.WriteInt16LittleEndian(header[34..36], 16);
        Encoding.ASCII.GetBytes("data", header[36..40]);
        BinaryPrimitives.WriteInt32LittleEndian(header[40..44], 0); // patched as data arrives
        _stream.Write(header);
    }

    private void PatchLengths()
    {
        var end = _stream.Position;
        Span<byte> value = stackalloc byte[4];

        _stream.Position = 4;
        BinaryPrimitives.WriteInt32LittleEndian(value, (int)(36 + _dataBytes));
        _stream.Write(value);

        _stream.Position = 40;
        BinaryPrimitives.WriteInt32LittleEndian(value, (int)_dataBytes);
        _stream.Write(value);

        _stream.Position = end;
    }
}
