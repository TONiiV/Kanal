namespace Kanal.Core.Meetings;

// Addressed by absolute position on the timeline, not by an index into the ring: a caller asking
// for audio that has already been overwritten is told no, rather than handed what replaced it.
internal sealed class RecentAudioBuffer
{
    private readonly byte[] _buffer;
    private long _written;

    internal RecentAudioBuffer(int capacityBytes) => _buffer = new byte[Math.Max(1, capacityBytes)];

    private long OldestByte => Math.Max(0, _written - _buffer.Length);

    internal void Write(ReadOnlySpan<byte> pcm16)
    {
        if (pcm16.Length == 0)
            return;

        var kept = pcm16.Length > _buffer.Length ? pcm16[^_buffer.Length..] : pcm16;
        var index = (int)((_written + (pcm16.Length - kept.Length)) % _buffer.Length);
        var toEnd = Math.Min(kept.Length, _buffer.Length - index);
        kept[..toEnd].CopyTo(_buffer.AsSpan(index));
        kept[toEnd..].CopyTo(_buffer.AsSpan(0));
        _written += pcm16.Length;
    }

    internal bool TryRead(long startByte, long endByte, out ReadOnlyMemory<byte> pcm16)
    {
        pcm16 = default;
        if (startByte < OldestByte || endByte > _written || endByte < startByte)
            return false;

        var result = new byte[endByte - startByte];
        var index = (int)(startByte % _buffer.Length);
        var toEnd = Math.Min(result.Length, _buffer.Length - index);
        _buffer.AsSpan(index, toEnd).CopyTo(result);
        _buffer.AsSpan(0, result.Length - toEnd).CopyTo(result.AsSpan(toEnd));
        pcm16 = result;
        return true;
    }
}
