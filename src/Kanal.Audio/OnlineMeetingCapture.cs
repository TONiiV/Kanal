using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Threading.Channels;

namespace Kanal.Audio;

public sealed record CaptureDiagnostic(
    string Session, string Event, string Source, string Device,
    long ReceivedSamples = 0, long DroppedSamples = 0, long PaddedSamples = 0,
    double Peak = 0, double Rms = 0, string? Error = null);

public sealed class OnlineMeetingCapture(
    IAudioCaptureService microphone,
    IAudioCaptureService systemAudio,
    TimeProvider? timeProvider = null)
{
    public const string MicrophoneSource = "microphone";
    public const string SystemSource = "system";
    public const string MixerSource = "mixer";
    public const int FrameSamples = 320;
    public static readonly TimeSpan PlayoutDelay = TimeSpan.FromMilliseconds(100);
    public static readonly TimeSpan ClockStallLimit = TimeSpan.FromSeconds(2);
    public static readonly TimeSpan ConsumerStallLimit = TimeSpan.FromSeconds(60);
    public static readonly TimeSpan SourceBuffer = TimeSpan.FromSeconds(4);
    public static readonly TimeSpan MaxSourceLead = TimeSpan.FromMilliseconds(500);
    public static readonly TimeSpan LevelInterval = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan MixInterval = TimeSpan.FromMilliseconds(20);

    public event Action<CaptureDiagnostic>? Diagnostic;

    public async IAsyncEnumerable<ReadOnlyMemory<byte>> CaptureAsync(
        string? microphoneId, string? outputId, [EnumeratorCancellation] CancellationToken ct)
    {
        var clock = timeProvider ?? TimeProvider.System;
        var origin = clock.GetTimestamp();
        var session = Guid.NewGuid().ToString("N");
        var diagnosticGate = new object();
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var mic = new SampleBuffer(clock, origin);
        var system = new SampleBuffer(clock, origin);
        var failure = new TaskCompletionSource<AudioCaptureException>(TaskCreationOptions.RunContinuationsAsynchronously);
        var output = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(
            (int)(Samples(ConsumerStallLimit) / FrameSamples))
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = true,
        });
        var pumps = new[]
        {
            Task.Run(() => Pump(microphone, microphoneId, MicrophoneSource, mic)),
            Task.Run(() => Pump(systemAudio, outputId, SystemSource, system)),
            Mix(),
        };
        try
        {
            while (true)
            {
                var read = output.Reader.WaitToReadAsync(ct).AsTask();
                await Task.WhenAny(read, failure.Task).ConfigureAwait(false);
                if (failure.Task.IsCompleted) throw await failure.Task;
                if (!await read) yield break;
                ct.ThrowIfCancellationRequested();
                if (output.Reader.TryRead(out var frame)) yield return frame;
            }
        }
        finally
        {
            await lifetime.CancelAsync();
            await Task.WhenAll(pumps).ConfigureAwait(false);
            Report("stopped", MicrophoneSource, microphoneId, mic);
            Report("stopped", SystemSource, outputId, system);
        }

        async Task Mix()
        {
            var delay = Samples(PlayoutDelay);
            var limit = Samples(ClockStallLimit);
            var next = -delay;
            var lastReport = origin;
            try
            {
                while (true)
                {
                    await Task.Delay(MixInterval, clock, lifetime.Token).ConfigureAwait(false);
                    var end = Samples(clock.GetElapsedTime(origin)) - delay;
                    if (end - next > limit)
                        throw new AudioCaptureException(AudioCaptureFault.ClockStalled,
                            $"Audio processing fell more than {ClockStallLimit.TotalSeconds:0} s behind the clock; the computer was busy or asleep.",
                            MixerSource);
                    while (next + FrameSamples <= end)
                    {
                        var a = mic.Read(next);
                        var b = system.Read(next);
                        var mixed = new short[FrameSamples];
                        for (var i = 0; i < mixed.Length; i++)
                            mixed[i] = MixSample(a[i], b[i]);
                        if (!output.Writer.TryWrite(PcmConvert.ShortsToBytes(mixed)))
                            throw new AudioCaptureException(AudioCaptureFault.ConsumerStalled,
                                $"The transcription connection accepted no audio for {ConsumerStallLimit.TotalSeconds:0} s.",
                                MixerSource);
                        next += FrameSamples;
                    }

                    if (clock.GetElapsedTime(lastReport) >= LevelInterval)
                    {
                        Report("levels", MicrophoneSource, microphoneId, mic);
                        Report("levels", SystemSource, outputId, system);
                        lastReport = clock.GetTimestamp();
                    }
                }
            }
            catch (Exception) when (lifetime.IsCancellationRequested) { }
            catch (Exception error)
            {
                await FailAsync(AudioCaptureException.Classify(error, MixerSource), null, null);
            }
        }

        async Task Pump(IAudioCaptureService source, string? id, string name, SampleBuffer buffer)
        {
            Report("starting", name, id, buffer);
            try
            {
                var first = true;
                await foreach (var frame in source.CaptureAsync(id, lifetime.Token).ConfigureAwait(false))
                {
                    buffer.Write(frame.Span);
                    if (first && frame.Length > 0)
                    {
                        first = false;
                        Report("first_frame", name, id, buffer);
                    }
                }

                if (!lifetime.IsCancellationRequested)
                    throw new AudioCaptureException(AudioCaptureFault.SourceEnded,
                        $"The {name} source stopped delivering audio; the device was probably unplugged or switched off.",
                        name);
            }
            catch (Exception) when (lifetime.IsCancellationRequested) { }
            catch (Exception error)
            {
                await FailAsync(AudioCaptureException.Classify(error, name), id, buffer);
            }
        }

        async Task FailAsync(AudioCaptureException fault, string? id, SampleBuffer? buffer)
        {
            Report("fault", fault.SourceName ?? MixerSource, id, buffer, fault.Code);
            failure.TrySetResult(fault);
            await lifetime.CancelAsync();
        }

        void Report(string kind, string source, string? id, SampleBuffer? buffer, string? error = null)
        {
            var levels = buffer?.Snapshot(resetWindow: kind == "levels") ?? default;
            var device = source == MixerSource ? "" : AudioDeviceIds.Hash(id);
            lock (diagnosticGate)
            {
                try
                {
                    Diagnostic?.Invoke(new(session, kind, source, device, levels.Received, levels.Dropped,
                        levels.Padded, levels.Peak, levels.Rms, error));
                }
                catch { }
            }
        }
    }

    private static readonly double SourceGain = Math.Sqrt(0.5);

    private static short MixSample(short microphone, short system) =>
        (short)Math.Clamp(Math.Round((microphone + system) * SourceGain), short.MinValue, short.MaxValue);

    private static long Samples(TimeSpan duration) =>
        (long)(duration.TotalSeconds * AudioCaptureFormat.SampleRateHz);

    private readonly record struct Levels(long Received, long Dropped, long Padded, double Peak, double Rms);

    private sealed class SampleBuffer(TimeProvider clock, long origin)
    {
        private static readonly long Lead = Samples(MaxSourceLead);
        private readonly object _gate = new();
        private readonly short[] _values = new short[Samples(SourceBuffer)];
        private readonly long[] _indices = Enumerable.Repeat(long.MinValue, (int)Samples(SourceBuffer)).ToArray();
        private long _received, _dropped, _padded, _readThrough = -Samples(PlayoutDelay);
        private long? _writeEnd;
        private int _windowPeak;
        private double _windowSquares;
        private long _windowCount;

        public void Write(ReadOnlySpan<byte> bytes)
        {
            if (bytes.Length % 2 != 0) throw new InvalidDataException("PCM16 requires complete samples.");
            var count = bytes.Length / 2;
            lock (_gate)
            {
                // No adapter delivers hardware timestamps, so arrival time stands in for capture time.
                var arrival = Samples(clock.GetElapsedTime(origin));
                // Continuity holds only while the cursor is unread: a fixed snap window loses audio to clock drift.
                var start = _writeEnd is { } previous && previous >= _readThrough ? previous : arrival - count;
                start = Math.Max(start, _readThrough);
                var limit = Math.Min(arrival + Lead, _readThrough + _values.Length);
                _writeEnd = Math.Min(start + count, Math.Max(start, limit));
                _received += count;
                for (var i = 0; i < count; i++)
                {
                    var value = BinaryPrimitives.ReadInt16LittleEndian(bytes.Slice(i * 2, 2));
                    _windowPeak = Math.Max(_windowPeak, Math.Abs((int)value));
                    _windowSquares += (double)value * value;
                    var index = start + i;
                    if (index >= limit) { _dropped++; continue; }
                    var slot = Slot(index);
                    _values[slot] = value;
                    _indices[slot] = index;
                }

                _windowCount += count;
            }
        }

        public short[] Read(long start)
        {
            var result = new short[FrameSamples];
            lock (_gate)
            {
                for (var i = 0; i < result.Length; i++)
                {
                    var slot = Slot(start + i);
                    if (_indices[slot] == start + i)
                    {
                        result[i] = _values[slot];
                        _indices[slot] = long.MinValue;
                    }
                    else _padded++;
                }

                _readThrough = start + result.Length;
            }

            return result;
        }

        public Levels Snapshot(bool resetWindow)
        {
            lock (_gate)
            {
                var levels = new Levels(_received, _dropped, _padded, _windowPeak / 32768.0,
                    _windowCount == 0 ? 0 : Math.Sqrt(_windowSquares / _windowCount) / 32768.0);
                if (resetWindow)
                {
                    _windowPeak = 0;
                    _windowSquares = 0;
                    _windowCount = 0;
                }

                return levels;
            }
        }

        private int Slot(long index) => (int)((index % _values.Length + _values.Length) % _values.Length);
    }
}
