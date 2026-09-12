namespace Kanal.Core.Meetings;

public enum AudioAvailability
{
    Available,
    UnknownUtterance,
    NotRecorded,
    AgedOut,
}

public sealed record TimelineSpan(long StartByte, long EndByte, bool EndIsOpen)
{
    public long ByteCount => EndByte - StartByte;
}

public sealed record RecordedSpan(
    AudioAvailability Availability,
    string Path,
    long StartDataByte,
    long EndDataByte)
{
    internal static RecordedSpan None(AudioAvailability availability) =>
        new(availability, "", 0, 0);
}

public sealed record RecentAudio(AudioAvailability Availability, ReadOnlyMemory<byte> Pcm16);

// Measured in bytes of accepted audio, never in wall-clock time: a minute off the record leaves
// no gap to account for, so the sentence after a pause lands where its audio actually is.
public sealed class MeetingTimeline
{
    public const int SampleRateHz = 16_000;
    public const int BytesPerSample = 2;
    public const int BytesPerSecond = SampleRateHz * BytesPerSample;

    public static readonly TimeSpan DefaultRecentAudioWindow = TimeSpan.FromSeconds(60);

    private readonly object _gate = new();
    private readonly RecentAudioBuffer _recent;
    private readonly Dictionary<string, Placement> _placed = new(StringComparer.Ordinal);
    private readonly List<Segment> _segments = new();
    private long _lengthBytes;
    private long _asrClockZero;

    public MeetingTimeline(TimeSpan? recentAudioWindow = null) =>
        _recent = new RecentAudioBuffer(
            (int)BytesFor((long)(recentAudioWindow ?? DefaultRecentAudioWindow).TotalMilliseconds));

    public long LengthBytes
    {
        get
        {
            lock (_gate)
                return _lengthBytes;
        }
    }

    public TimeSpan Duration => TimeSpan.FromSeconds(LengthBytes / (double)BytesPerSecond);

    public void Append(ReadOnlySpan<byte> pcm16)
    {
        lock (_gate)
        {
            _recent.Write(pcm16);
            _lengthBytes += pcm16.Length;
        }
    }

    public void AsrClockRestarted()
    {
        lock (_gate)
            _asrClockZero = _lengthBytes;
    }

    public void RecordingStarted(string path)
    {
        lock (_gate)
        {
            CloseOpenSegment();
            _segments.Add(new Segment(path, _lengthBytes, null));
        }
    }

    public void RecordingStopped()
    {
        lock (_gate)
            CloseOpenSegment();
    }

    public void Observe(string utteranceId, long tStartMs, long? tEndMs)
    {
        lock (_gate)
        {
            // A restart between a sentence's partial and its final must not re-place the
            // sentence on the new clock, so the clock is pinned when the sentence is first seen.
            var clockZero = _placed.TryGetValue(utteranceId, out var existing)
                ? existing.ClockZero
                : _asrClockZero;

            var start = Clamp(clockZero + BytesFor(tStartMs));
            var end = tEndMs is { } endMs ? Clamp(clockZero + BytesFor(endMs)) : _lengthBytes;
            _placed[utteranceId] = new Placement(
                clockZero, start, Math.Max(start, end), EndIsOpen: tEndMs is null);
        }
    }

    public TimelineSpan? Locate(string utteranceId)
    {
        lock (_gate)
            return _placed.TryGetValue(utteranceId, out var placement)
                ? new TimelineSpan(placement.StartByte, placement.EndByte, placement.EndIsOpen)
                : null;
    }

    public RecordedSpan LocateInRecording(string utteranceId)
    {
        lock (_gate)
        {
            if (!_placed.TryGetValue(utteranceId, out var placement))
                return RecordedSpan.None(AudioAvailability.UnknownUtterance);

            foreach (var segment in _segments)
            {
                var segmentEnd = segment.EndByte ?? _lengthBytes;
                if (placement.StartByte < segment.StartByte || placement.StartByte >= segmentEnd)
                    continue;

                return new RecordedSpan(
                    AudioAvailability.Available,
                    segment.Path,
                    placement.StartByte - segment.StartByte,
                    Math.Min(placement.EndByte, segmentEnd) - segment.StartByte);
            }

            return RecordedSpan.None(AudioAvailability.NotRecorded);
        }
    }

    public RecentAudio TakeRecentAudio(string utteranceId)
    {
        lock (_gate)
        {
            if (!_placed.TryGetValue(utteranceId, out var placement))
                return new RecentAudio(AudioAvailability.UnknownUtterance, default);

            return _recent.TryRead(placement.StartByte, placement.EndByte, out var pcm16)
                ? new RecentAudio(AudioAvailability.Available, pcm16)
                : new RecentAudio(AudioAvailability.AgedOut, default);
        }
    }

    private void CloseOpenSegment()
    {
        if (_segments.Count > 0 && _segments[^1].EndByte is null)
            _segments[^1] = _segments[^1] with { EndByte = _lengthBytes };
    }

    // Clamping here is what leaves the ring buffer one failure to report: aged out, never unwritten.
    private long Clamp(long byteOffset) => Math.Clamp(byteOffset, 0, _lengthBytes);

    private static long BytesFor(long milliseconds)
    {
        var bytes = Math.Max(0, milliseconds) * BytesPerSecond / 1_000;
        return bytes - bytes % BytesPerSample;
    }

    private readonly record struct Placement(
        long ClockZero, long StartByte, long EndByte, bool EndIsOpen);

    private readonly record struct Segment(string Path, long StartByte, long? EndByte);
}
