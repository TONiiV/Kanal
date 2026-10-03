using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading.Channels;
using Kanal.Audio;

namespace Kanal.Core.UnitTests;

public class OnlineMeetingCaptureTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(5);
    private const int Delay = 1600;

    [Fact]
    public async Task CallbackJitterPreservesContinuousSamples()
    {
        await using var run = await Run.StartAsync();
        await run.Clock.StepAsync(20);
        await run.Mic.Send(Constant(1000, 320));
        await run.Clock.StepAsync(25);
        await run.Mic.Send(Constant(2000, 320));
        await run.Clock.StepAsync(15);
        await run.Mic.Send(Constant(3000, 320));
        await run.Clock.StepAsync(100);

        var samples = await run.TakeAsync(8);
        AssertConstant(0, samples[..Delay]);
        AssertConstant(707, samples[Delay..(Delay + 320)]);
        AssertConstant(1414, samples[(Delay + 320)..(Delay + 640)]);
        AssertConstant(2121, samples[(Delay + 640)..]);
    }

    [Fact]
    public async Task ASystemSourceThatNeverDeliversPadsSilenceWithoutFaulting()
    {
        await using var run = await Run.StartAsync();
        var pattern = Pattern(Delay * 10);
        for (var block = 0; block < 10; block++)
        {
            await run.Clock.StepAsync(100);
            await run.Mic.Send(pattern[(block * Delay)..((block + 1) * Delay)]);
        }
        await run.Clock.IdleAsync();

        var samples = await run.TakeAsync(50);
        Assert.Equal(Attenuated(pattern[..(Delay * 9)]), samples[Delay..]);
        Assert.Null(run.Fault);
    }

    [Fact]
    public async Task ASingleTalkerIsAttenuatedByThreeDecibelsNotSix()
    {
        await using var run = await Run.StartAsync();
        await run.Clock.StepAsync(20);
        await run.Mic.Send(Constant(10000, 320));
        await run.Clock.StepAsync(20);
        await run.System.Send(Constant(-10000, 320));
        await run.Clock.StepAsync(100);

        var samples = await run.TakeAsync(7);
        AssertConstant(7071, samples[Delay..(Delay + 320)]);
        AssertConstant(-7071, samples[(Delay + 320)..(Delay + 640)]);
    }

    [Fact]
    public async Task TwoFullScaleSourcesSaturateInsteadOfWrapping()
    {
        await using var run = await Run.StartAsync();
        await run.Clock.StepAsync(20);
        await Task.WhenAll(run.Mic.Send(Constant(32767, 320)), run.System.Send(Constant(32767, 320)));
        await run.Clock.StepAsync(20);
        await Task.WhenAll(run.Mic.Send(Constant(-32768, 320)), run.System.Send(Constant(-32768, 320)));
        await run.Clock.StepAsync(20);
        await Task.WhenAll(run.Mic.Send(Constant(20000, 320)), run.System.Send(Constant(-20000, 320)));
        await run.Clock.StepAsync(100);

        var samples = await run.TakeAsync(8);
        AssertConstant(short.MaxValue, samples[Delay..(Delay + 320)]);
        AssertConstant(short.MinValue, samples[(Delay + 320)..(Delay + 640)]);
        AssertConstant(0, samples[(Delay + 640)..(Delay + 960)]);
    }

    [Fact]
    public async Task DifferentCallbackSizesAlignTheSamePhysicalInterval()
    {
        await using var run = await Run.StartAsync();
        await run.Clock.StepAsync(20);
        await run.System.Send(Constant(2000, 320));
        await run.Clock.StepAsync(80);
        await run.Mic.Send([.. Constant(1000, 320), .. new short[1280]]);
        await run.Clock.StepAsync(20);

        var samples = await run.TakeAsync(6);
        AssertConstant(2121, samples[Delay..]);
    }

    [Fact]
    public async Task ATwentySecondConsumerStallLosesAndCompressesNothing()
    {
        await using var run = await Run.StartAsync();
        const int seconds = 20;
        var pattern = Pattern(16000 * seconds);
        for (var block = 0; block < pattern.Length / Delay; block++)
        {
            await run.Clock.StepAsync(100);
            await run.Mic.Send(pattern[(block * Delay)..((block + 1) * Delay)]);
        }
        await run.Clock.IdleAsync();

        var samples = await run.TakeAsync(16000 * seconds / 320);
        Assert.Equal(16000 * seconds, samples.Length);
        Assert.Equal(Attenuated(pattern[..^Delay]), samples[Delay..]);
        Assert.False(run.HasMore);
        Assert.Null(run.Fault);
    }

    public static TheoryData<int, int, int> SlowSourceClocks => new()
    {
        { 799, 50, 180 },
        { 1598, 100, 180 },
        { 319, 20, 120 },
    };

    [Theory]
    [MemberData(nameof(SlowSourceClocks))]
    public async Task ASourceClockRunningSlowLosesNoSamplesOverMinutes(int chunk, int periodMs, int seconds)
    {
        await using var run = await Run.StartAsync();
        var drain = run.Drain();
        var chunks = seconds * 1000 / periodMs;

        await run.SendNumberedChunksAsync(chunks, chunk, periodMs);
        var mic = await run.FinishAsync(drain, tailMs: 100);

        var sent = (long)chunk * chunks;
        var deficit = (long)chunks * periodMs * 16 - sent;
        Assert.Equal(0, mic.DroppedSamples);
        Assert.Equal(sent, drain.Audible);
        Assert.Empty(drain.Incomplete(0, chunks, chunk, chunk));
        Assert.Equal(deficit + Delay, mic.PaddedSamples);
    }

    [Fact]
    public async Task ACallbackLateBeyondThePlayoutDelayIsPlacedAfterWhatWasReadNotDropped()
    {
        await using var run = await Run.StartAsync();
        var drain = run.Drain();
        int[] gaps = [50, 50, 50, 50, 50, 50, 50, 50, 50, 50, 180, 0, 0, 50, 50, 50, 50, 50];
        for (var k = 0; k < gaps.Length; k++)
        {
            await run.Clock.StepAsync(gaps[k]);
            await run.Mic.Send(Constant(ChunkValue(k), 800));
        }
        var mic = await run.FinishAsync(drain, tailMs: 400);

        Assert.Equal(0, mic.DroppedSamples);
        Assert.Equal(800L * gaps.Length, drain.Audible);
        Assert.Empty(drain.Incomplete(0, gaps.Length, 800, 800));
    }

    public static TheoryData<int, int, int> FastSourceClocks => new()
    {
        { 804, 50, 180 },
        { 1608, 100, 180 },
        { 322, 20, 120 },
    };

    [Theory]
    [MemberData(nameof(FastSourceClocks))]
    public async Task ASourceClockRunningFastNeverOverwritesUnreadAudio(int chunk, int periodMs, int seconds)
    {
        await using var run = await Run.StartAsync();
        var drain = run.Drain();
        var chunks = seconds * 1000 / periodMs;
        var excess = (int)(chunk - periodMs * 16L);

        await run.SendNumberedChunksAsync(chunks, chunk, periodMs);
        var mic = await run.FinishAsync(drain, tailMs: 100);

        var sent = (long)chunk * chunks;
        var lead = OnlineMeetingCapture.MaxSourceLead.TotalSeconds * 16000;
        var unread = sent - drain.Audible - mic.DroppedSamples;
        Assert.InRange(unread, 0, lead + chunk);
        Assert.True(mic.DroppedSamples > 0, "the source ran far enough ahead to reach the lead limit");
        Assert.InRange(mic.PaddedSamples, 0, Delay);
        Assert.Empty(drain.Incomplete(0, chunks - (int)(lead / chunk) - 2, chunk - 2 * excess, chunk));
    }

    [Fact]
    public async Task ASustainedConsumerStallFaultsAsConsumerStalled()
    {
        await using var run = await Run.StartAsync();
        var steps = (int)(OnlineMeetingCapture.ConsumerStallLimit.TotalMilliseconds / 100) + 2;
        for (var i = 0; i < steps && run.Fault is null; i++)
            await run.Clock.StepAsync(100, run.Faulted);

        var error = await Assert.ThrowsAsync<AudioCaptureException>(() => run.DrainAsync());
        Assert.Equal("consumer_stalled", error.Code);
        Assert.Equal("consumer_stalled", run.Fault?.Error);
        Assert.Equal(OnlineMeetingCapture.MixerSource, run.Fault?.Source);
        Assert.True(run.Mic.Stopped && run.System.Stopped);
    }

    [Fact]
    public async Task AShortTimerStallCatchesUpWithContinuousSamples()
    {
        await using var run = await Run.StartAsync();
        var pattern = Pattern(16000);
        for (var block = 0; block < 10; block++)
        {
            run.Clock.AdvanceSilently(100);
            await run.Mic.Send(pattern[(block * Delay)..((block + 1) * Delay)]);
        }
        await run.Clock.StepAsync(0);
        await run.Clock.IdleAsync();

        var samples = await run.TakeAsync(50);
        Assert.Equal(Attenuated(pattern[..^Delay]), samples[Delay..]);
        Assert.False(run.HasMore);
        Assert.Null(run.Fault);
    }

    [Fact]
    public async Task ALongTimerStallFaultsAsClockStalled()
    {
        await using var run = await Run.StartAsync();
        run.Clock.AdvanceSilently((int)OnlineMeetingCapture.ClockStallLimit.TotalMilliseconds + 200);
        await run.Clock.StepAsync(0, run.Faulted);

        var error = await Assert.ThrowsAsync<AudioCaptureException>(() => run.DrainAsync());
        Assert.Equal("clock_stalled", error.Code);
        Assert.Equal(OnlineMeetingCapture.MixerSource, error.SourceName);
    }

    [Fact]
    public async Task LevelsCoverEverythingReceivedSinceTheLastReport()
    {
        await using var run = await Run.StartAsync();
        await run.Clock.StepAsync(20);
        await run.Mic.Send(Constant(16384, 320));
        for (var i = 0; i < 12; i++)
        {
            await run.Clock.StepAsync(20);
            await run.Mic.Send(new short[320]);
        }
        await run.Clock.IdleAsync();

        var levels = run.Diagnostics.First(d => d is { Event: "levels", Source: OnlineMeetingCapture.MicrophoneSource });
        Assert.Equal(0.5, levels.Peak, 3);
        Assert.InRange(levels.Rms, 0.1, 0.2);
        var silent = run.Diagnostics.First(d => d is { Event: "levels", Source: OnlineMeetingCapture.SystemSource });
        Assert.Equal(0, silent.Peak);
        Assert.Equal(0, silent.ReceivedSamples);
    }

    [Fact]
    public async Task ConsumerCancellationDisposesBothSources()
    {
        using var cancel = new CancellationTokenSource();
        await using var run = await Run.StartAsync(consumer: cancel);
        await run.Clock.StepAsync(20);

        await cancel.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run.DrainAsync());
        Assert.True(run.Mic.Stopped);
        Assert.True(run.System.Stopped);
    }

    [Fact]
    public async Task APartialStartFailureCancelsAndReleasesTheOtherSource()
    {
        var mic = new Input { HangOnStart = true };
        var system = new Input { FailOnStart = new UnauthorizedAccessException("denied") };
        await using var run = await Run.StartAsync(mic: mic, system: system, waitForStart: false);
        await mic.Started.Task.WaitAsync(Patience, TestContext.Current.CancellationToken);

        var error = await Assert.ThrowsAsync<AudioCaptureException>(() => run.DrainAsync());
        Assert.Equal("permission_denied", error.Code);
        Assert.Equal(OnlineMeetingCapture.SystemSource, error.SourceName);
        Assert.True(mic.Cancelled);
        Assert.True(mic.Stopped);
    }

    [Fact]
    public async Task ADisappearingSourceFaultsAsSourceEnded()
    {
        await using var run = await Run.StartAsync();
        await run.Clock.StepAsync(20);

        run.System.End();

        var error = await Assert.ThrowsAsync<AudioCaptureException>(() => run.DrainAsync());
        Assert.Equal("source_ended", error.Code);
        Assert.Equal(OnlineMeetingCapture.SystemSource, error.SourceName);
        Assert.True(run.Mic.Stopped);
        var fault = Assert.Single(run.Diagnostics, d => d.Event == "fault");
        Assert.Equal(("system", "source_ended"), (fault.Source, fault.Error));
    }

    public static TheoryData<string, string, string> SourceFaults => new()
    {
        { "unauthorized", "microphone", "permission_denied" },
        { "access-denied-hresult", "system", "permission_denied" },
        { "permission-coded", "system", "permission_denied" },
        { "not-found-hresult", "system", "device_unavailable" },
        { "invalidated-hresult", "microphone", "device_unavailable" },
        { "unavailable-coded", "microphone", "device_unavailable" },
        { "anything-else", "system", "source_failed" },
    };

    [Theory]
    [MemberData(nameof(SourceFaults))]
    public async Task SourceFailuresAreReportedWithAClosedCodeAndTheSource(string kind, string source, string code)
    {
        Exception error = kind switch
        {
            "unauthorized" => new UnauthorizedAccessException("denied"),
            "access-denied-hresult" => new COMException("denied", unchecked((int)0x80070005)),
            "permission-coded" => new AudioCaptureException(AudioCaptureFault.PermissionDenied, "tcc"),
            "not-found-hresult" => new COMException("gone", unchecked((int)0x80070490)),
            "invalidated-hresult" => new COMException("gone", unchecked((int)0x88890004)),
            "unavailable-coded" => new AudioCaptureException(AudioCaptureFault.DeviceUnavailable, "stale"),
            _ => new InvalidOperationException("boom"),
        };
        var mic = new Input();
        var system = new Input();
        await using var run = await Run.StartAsync(mic: mic, system: system);

        (source == "microphone" ? mic : system).Fail(error);

        var fault = await Assert.ThrowsAsync<AudioCaptureException>(() => run.DrainAsync());
        Assert.Equal(code, fault.Code);
        Assert.Equal(source, fault.SourceName);
        Assert.Contains(run.Diagnostics, d => d.Event == "fault" && d.Source == source && d.Error == code);
        Assert.True(mic.Stopped && system.Stopped);
    }

    private static short[] Constant(short value, int count) => Enumerable.Repeat(value, count).ToArray();

    private static short[] Pattern(int count) =>
        Enumerable.Range(0, count).Select(i => (short)(2 * (i % 15000 + 1))).ToArray();

    private static void AssertConstant(short expected, short[] segment) =>
        Assert.Equal([expected], segment.Distinct().ToArray());

    private static short Attenuated(short sample) => (short)Math.Round(sample * Math.Sqrt(0.5));

    private static short[] Attenuated(short[] samples) => samples.Select(Attenuated).ToArray();

    private static short ChunkValue(int k) => (short)(2 * (k + 1));

    private static int ChunkOf(short mixed) => (int)Math.Round(mixed / Math.Sqrt(0.5) / 2) - 1;

    internal sealed class Drainer
    {
        private readonly Dictionary<int, int> _perChunk = [];
        private long _frames, _audible;

        public Task Task { get; set; } = Task.CompletedTask;
        public long Frames => Interlocked.Read(ref _frames);
        public long Audible => Interlocked.Read(ref _audible);

        public (int Chunk, int Samples)[] Incomplete(int from, int to, int min, int max)
        {
            lock (_perChunk)
                return Enumerable.Range(from, to - from)
                    .Select(k => (k, _perChunk.GetValueOrDefault(k)))
                    .Where(c => c.Item2 < min || c.Item2 > max)
                    .Take(5)
                    .ToArray();
        }

        public void Add(ReadOnlySpan<byte> frame)
        {
            var audible = 0;
            lock (_perChunk)
            {
                foreach (var sample in PcmConvert.BytesToShorts(frame))
                {
                    if (sample == 0) continue;
                    audible++;
                    var chunk = ChunkOf(sample);
                    _perChunk[chunk] = _perChunk.GetValueOrDefault(chunk) + 1;
                }
            }
            Interlocked.Add(ref _audible, audible);
            Interlocked.Increment(ref _frames);
        }
    }

    private sealed class Run : IAsyncDisposable
    {
        private readonly IAsyncEnumerator<ReadOnlyMemory<byte>> _stream;
        private readonly List<CaptureDiagnostic> _diagnostics = [];
        private readonly TaskCompletionSource _faulted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly CancellationTokenSource _cts;
        private Task<bool> _next;
        private bool _disposed;

        private Run(Input mic, Input system, CancellationToken ct)
        {
            Mic = mic;
            System = system;
            _cts = CancellationTokenSource.CreateLinkedTokenSource(ct, TestContext.Current.CancellationToken);
            ct = _cts.Token;
            var capture = new OnlineMeetingCapture(mic, system, Clock);
            capture.Diagnostic += d =>
            {
                lock (_diagnostics)
                    _diagnostics.Add(d);
                if (d.Event == "fault")
                    _faulted.TrySetResult();
            };
            _stream = capture.CaptureAsync("mic", "output", ct).GetAsyncEnumerator(ct);
            _next = _stream.MoveNextAsync().AsTask();
        }

        public ManualClock Clock { get; } = new();
        public Input Mic { get; }
        public Input System { get; }
        public Task Faulted => _faulted.Task;
        public bool HasMore => _next.IsCompleted;

        public IReadOnlyList<CaptureDiagnostic> Diagnostics
        {
            get { lock (_diagnostics) return _diagnostics.ToArray(); }
        }

        public CaptureDiagnostic? Fault => Diagnostics.FirstOrDefault(d => d.Event == "fault");

        public static async Task<Run> StartAsync(
            Input? mic = null, Input? system = null, bool waitForStart = true, CancellationTokenSource? consumer = null)
        {
            var run = new Run(mic ?? new Input(), system ?? new Input(), consumer?.Token ?? default);
            if (waitForStart)
            {
                await Task.WhenAll(run.Mic.Started.Task, run.System.Started.Task).WaitAsync(Patience);
                await run.Clock.IdleAsync();
            }
            return run;
        }

        public async Task<short[]> TakeAsync(int frames)
        {
            var samples = new List<short>();
            for (var i = 0; i < frames; i++)
            {
                Assert.True(await _next.WaitAsync(Patience), "the capture ended early");
                samples.AddRange(PcmConvert.BytesToShorts(_stream.Current.Span));
                _next = _stream.MoveNextAsync().AsTask();
            }
            return [.. samples];
        }

        public async Task DrainAsync()
        {
            while (await _next.WaitAsync(Patience))
                _next = _stream.MoveNextAsync().AsTask();
        }

        public Drainer Drain()
        {
            var drainer = new Drainer();
            drainer.Task = Task.Run(async () =>
            {
                try
                {
                    while (await _next)
                    {
                        drainer.Add(_stream.Current.Span);
                        _next = _stream.MoveNextAsync().AsTask();
                    }
                }
                catch (OperationCanceledException) { }
            });
            return drainer;
        }

        public async Task SendNumberedChunksAsync(int chunks, int chunk, int periodMs)
        {
            for (var k = 0; k < chunks; k++)
            {
                await Clock.StepAsync(periodMs);
                await Mic.Send(Constant(ChunkValue(k), chunk));
            }
        }

        public async Task<CaptureDiagnostic> FinishAsync(Drainer drainer, int tailMs)
        {
            await Clock.StepAsync(tailMs);
            await Clock.IdleAsync();
            var frames = Clock.GetTimestamp() * 16000 / TimeSpan.TicksPerSecond / OnlineMeetingCapture.FrameSamples;
            var deadline = DateTime.UtcNow + Patience;
            while (drainer.Frames < frames)
            {
                Assert.True(DateTime.UtcNow < deadline, $"drained {drainer.Frames} of {frames} frames");
                await Task.Delay(1);
            }
            Assert.Null(Fault);
            await DisposeAsync();
            await drainer.Task.WaitAsync(Patience);
            return Diagnostics.Last(d => d is { Event: "stopped", Source: OnlineMeetingCapture.MicrophoneSource });
        }

        public async ValueTask DisposeAsync()
        {
            if (_disposed) return;
            _disposed = true;
            await _cts.CancelAsync();
            try { await _next.WaitAsync(Patience); }
            catch (Exception error) when (error is not TimeoutException) { }
            await _stream.DisposeAsync().AsTask().WaitAsync(Patience);
            _cts.Dispose();
        }
    }

    internal sealed class Input : IAudioCaptureService
    {
        private readonly Channel<(byte[] Data, TaskCompletionSource Ack)> _frames =
            Channel.CreateUnbounded<(byte[], TaskCompletionSource)>();
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool HangOnStart { get; init; }
        public Exception? FailOnStart { get; init; }
        public bool Stopped { get; private set; }
        public bool Cancelled { get; private set; }

        public IReadOnlyList<AudioDeviceInfo> GetDevices() => [new("mic", "Microphone"), new("output", "Output")];

        public Task Send(short[] samples)
        {
            var ack = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _frames.Writer.TryWrite((PcmConvert.ShortsToBytes(samples), ack));
            return ack.Task.WaitAsync(Patience);
        }

        public void Fail(Exception error) => _frames.Writer.TryComplete(error);

        public void End() => _frames.Writer.TryComplete();

        public async IAsyncEnumerable<ReadOnlyMemory<byte>> CaptureAsync(
            string? deviceId, [EnumeratorCancellation] CancellationToken ct)
        {
            Started.TrySetResult();
            try
            {
                if (FailOnStart is not null)
                    throw FailOnStart;
                if (HangOnStart)
                {
                    try { await Task.Delay(Timeout.Infinite, ct); }
                    catch (OperationCanceledException) { Cancelled = true; throw; }
                }

                await foreach (var frame in _frames.Reader.ReadAllAsync(ct))
                {
                    yield return frame.Data;
                    frame.Ack.TrySetResult();
                }
            }
            finally { Stopped = true; }
        }
    }

    internal sealed class ManualClock : TimeProvider
    {
        private readonly object _gate = new();
        private readonly List<ManualTimer> _pending = [];
        private TaskCompletionSource _armed = New();
        private long _ticks;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp()
        {
            lock (_gate) return _ticks;
        }

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new ManualTimer(this, callback, state);
            timer.Change(dueTime, period);
            return timer;
        }

        public Task IdleAsync(Task? orFault = null)
        {
            Task armed;
            lock (_gate) armed = _armed.Task;
            return (orFault is null ? armed : Task.WhenAny(armed, orFault)).WaitAsync(Patience);
        }

        public async Task StepAsync(int milliseconds, Task? orFault = null)
        {
            await IdleAsync(orFault);
            Advance(milliseconds, fire: true);
        }

        public void AdvanceSilently(int milliseconds) => Advance(milliseconds, fire: false);

        private void Advance(int milliseconds, bool fire)
        {
            List<ManualTimer> due = [];
            lock (_gate)
            {
                _ticks += TimeSpan.FromMilliseconds(milliseconds).Ticks;
                if (fire)
                {
                    due = _pending.Where(t => t.Due <= _ticks).ToList();
                    foreach (var timer in due)
                        Remove(timer);
                }
            }
            foreach (var timer in due)
                timer.Fire();
        }

        private void Schedule(ManualTimer timer, TimeSpan dueTime)
        {
            lock (_gate)
            {
                Remove(timer);
                if (dueTime == Timeout.InfiniteTimeSpan)
                    return;
                timer.Due = _ticks + dueTime.Ticks;
                _pending.Add(timer);
                _armed.TrySetResult();
            }
        }

        private void Remove(ManualTimer timer)
        {
            if (_pending.Remove(timer) && _pending.Count == 0)
                _armed = New();
        }

        private void Cancel(ManualTimer timer)
        {
            lock (_gate) Remove(timer);
        }

        private static TaskCompletionSource New() => new(TaskCreationOptions.RunContinuationsAsynchronously);

        private sealed class ManualTimer(ManualClock clock, TimerCallback callback, object? state) : ITimer
        {
            public long Due { get; set; }
            public void Fire() => callback(state);
            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                clock.Schedule(this, dueTime);
                return true;
            }
            public void Dispose() => clock.Cancel(this);
            public ValueTask DisposeAsync()
            {
                Dispose();
                return ValueTask.CompletedTask;
            }
        }
    }
}
