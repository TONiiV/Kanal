using System.Buffers.Binary;
using System.Text;

namespace Kanal.Audio;

/// <summary>Minimal PCM16 WAV reader/writer — used for the D0 capture check and replay testing.</summary>
public static class WavFile
{
    public sealed record WavData(int SampleRateHz, int Channels, byte[] Pcm16);

    public static void Write(Stream stream, ReadOnlySpan<byte> pcm16, int sampleRateHz, int channels)
    {
        var byteRate = sampleRateHz * channels * 2;
        var blockAlign = (short)(channels * 2);

        Span<byte> header = stackalloc byte[44];
        Encoding.ASCII.GetBytes("RIFF", header[..4]);
        BinaryPrimitives.WriteInt32LittleEndian(header[4..8], 36 + pcm16.Length);
        Encoding.ASCII.GetBytes("WAVE", header[8..12]);
        Encoding.ASCII.GetBytes("fmt ", header[12..16]);
        BinaryPrimitives.WriteInt32LittleEndian(header[16..20], 16);
        BinaryPrimitives.WriteInt16LittleEndian(header[20..22], 1); // PCM
        BinaryPrimitives.WriteInt16LittleEndian(header[22..24], (short)channels);
        BinaryPrimitives.WriteInt32LittleEndian(header[24..28], sampleRateHz);
        BinaryPrimitives.WriteInt32LittleEndian(header[28..32], byteRate);
        BinaryPrimitives.WriteInt16LittleEndian(header[32..34], blockAlign);
        BinaryPrimitives.WriteInt16LittleEndian(header[34..36], 16);
        Encoding.ASCII.GetBytes("data", header[36..40]);
        BinaryPrimitives.WriteInt32LittleEndian(header[40..44], pcm16.Length);

        stream.Write(header);
        stream.Write(pcm16);
    }

    public static WavData Read(Stream stream)
    {
        using var reader = new BinaryReader(stream, Encoding.ASCII, leaveOpen: true);
        if (Encoding.ASCII.GetString(reader.ReadBytes(4)) != "RIFF")
            throw new InvalidDataException("Not a RIFF file.");
        reader.ReadInt32(); // riff size
        if (Encoding.ASCII.GetString(reader.ReadBytes(4)) != "WAVE")
            throw new InvalidDataException("Not a WAVE file.");

        int sampleRate = 0, channels = 0, bitsPerSample = 0;
        byte[]? data = null;
        while (stream.Position + 8 <= stream.Length)
        {
            var chunkId = Encoding.ASCII.GetString(reader.ReadBytes(4));
            var chunkSize = reader.ReadInt32();
            switch (chunkId)
            {
                case "fmt ":
                    var format = reader.ReadInt16();
                    if (format != 1 && format != -2) // PCM or extensible
                        throw new InvalidDataException($"Unsupported WAV format tag {format}; only PCM is supported.");
                    channels = reader.ReadInt16();
                    sampleRate = reader.ReadInt32();
                    reader.ReadInt32(); // byte rate
                    reader.ReadInt16(); // block align
                    bitsPerSample = reader.ReadInt16();
                    if (chunkSize > 16)
                        reader.ReadBytes(chunkSize - 16);
                    break;
                case "data":
                    data = reader.ReadBytes(chunkSize);
                    break;
                default:
                    reader.ReadBytes(chunkSize + (chunkSize % 2)); // chunks are word-aligned
                    break;
            }
        }

        if (data is null || sampleRate == 0)
            throw new InvalidDataException("Missing fmt or data chunk.");
        if (bitsPerSample != 16)
            throw new InvalidDataException($"Only 16-bit PCM is supported (got {bitsPerSample}-bit).");

        return new WavData(sampleRate, channels, data);
    }
}
