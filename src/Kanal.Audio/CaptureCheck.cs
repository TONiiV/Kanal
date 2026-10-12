namespace Kanal.Audio;

public sealed record SourceCheck(
    string Source, string State, long ReceivedSamples, long DroppedSamples, long PaddedSamples,
    double Peak, string? Fault);

public sealed class CaptureCheck
{
    public const int Ok = 0;
    public const int Failed = 2;
    public const int Silent = 3;
    public static readonly TimeSpan PrintInterval = TimeSpan.FromSeconds(5);

    private readonly object _gate = new();
    private readonly TimeProvider _clock;
    private readonly Dictionary<string, Tally> _sources;
    private string? _unattributed;

    public CaptureCheck(TimeProvider clock, params string[] sources)
    {
        _clock = clock;
        _sources = sources.ToDictionary(s => s, _ => new Tally(new SignalWatch(clock)));
    }

    public bool Observe(CaptureDiagnostic diagnostic)
    {
        lock (_gate)
        {
            if (!_sources.TryGetValue(diagnostic.Source, out var tally))
                return true;
            if (diagnostic.Event == "fault")
                tally.Fault ??= diagnostic.Error;
            if (diagnostic.Event != "levels")
                return true;

            Record(tally, diagnostic.ReceivedSamples, diagnostic.Peak);
            tally.Dropped = diagnostic.DroppedSamples;
            tally.Padded = diagnostic.PaddedSamples;
            var now = _clock.GetTimestamp();
            if (tally.Printed is { } printed && _clock.GetElapsedTime(printed, now) < PrintInterval)
                return false;
            tally.Printed = now;
            return true;
        }
    }

    public void Observe(string source, long receivedSamples, double peak)
    {
        lock (_gate)
            Record(_sources[source], receivedSamples, peak);
    }

    public void Fail(AudioCaptureException fault)
    {
        lock (_gate)
        {
            if (fault.SourceName is { } name && _sources.TryGetValue(name, out var tally))
                tally.Fault ??= fault.Code;
            else
                _unattributed ??= fault.Code;
        }
    }

    public IReadOnlyList<SourceCheck> Summaries
    {
        get
        {
            lock (_gate)
                return _sources.Select(s => new SourceCheck(
                    s.Key, SignalWatch.CodeOf(s.Value.Watch.Conclusion), s.Value.Received, s.Value.Dropped,
                    s.Value.Padded, s.Value.Peak, s.Value.Fault)).ToList();
        }
    }

    public IReadOnlyList<string> SilentSources
    {
        get
        {
            lock (_gate)
                return _sources
                    .Where(s => s.Value.Watch.Conclusion is not (SignalState.Sound or SignalState.Pending))
                    .Select(s => s.Key)
                    .ToList();
        }
    }

    public int ExitCode
    {
        get
        {
            lock (_gate)
                if (_unattributed is not null || _sources.Values.Any(t => t.Fault is not null))
                    return Failed;
            return SilentSources.Count > 0 ? Silent : Ok;
        }
    }

    private static void Record(Tally tally, long receivedSamples, double peak)
    {
        tally.Watch.Observe(receivedSamples, peak);
        tally.Received = Math.Max(tally.Received, receivedSamples);
        tally.Peak = Math.Max(tally.Peak, peak);
    }

    private sealed class Tally(SignalWatch watch)
    {
        public SignalWatch Watch { get; } = watch;
        public long Received;
        public long Dropped;
        public long Padded;
        public double Peak;
        public string? Fault;
        public long? Printed;
    }
}
