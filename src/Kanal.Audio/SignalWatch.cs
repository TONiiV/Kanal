using System.Text.Json;

namespace Kanal.Audio;

public enum SignalState
{
    Pending,
    Sound,
    NoSignal,
    SilentSignal,
    WentQuiet,
}

public sealed class SignalWatch(TimeProvider clock, TimeSpan? grace = null)
{
    public const double SoundPeak = 0.003;
    public static readonly TimeSpan StartupGrace = TimeSpan.FromSeconds(8);
    public static readonly TimeSpan RemoteStartupGrace = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan QuietLimit = TimeSpan.FromSeconds(120);

    private readonly long _started = clock.GetTimestamp();
    private readonly TimeSpan _grace = grace ?? StartupGrace;
    private long? _lastSound;
    private long _received;

    public SignalState State { get; private set; } = SignalState.Pending;

    public bool HeardSound => _lastSound is not null;

    public bool IsSilent => State is SignalState.NoSignal or SignalState.SilentSignal or SignalState.WentQuiet;

    public SignalState Conclusion => HeardSound ? State : Unheard;

    private SignalState Unheard => _received > 0 ? SignalState.SilentSignal : SignalState.NoSignal;

    public static string CodeOf(SignalState state) =>
        JsonNamingPolicy.SnakeCaseLower.ConvertName(state.ToString());

    public bool Observe(long receivedSamples, double peak, bool mayConclude = true)
    {
        var now = clock.GetTimestamp();
        _received = Math.Max(_received, receivedSamples);
        if (peak >= SoundPeak)
            _lastSound = now;

        var next = _lastSound is { } heard
            ? clock.GetElapsedTime(heard, now) >= QuietLimit ? SignalState.WentQuiet : SignalState.Sound
            : mayConclude && clock.GetElapsedTime(_started, now) >= _grace ? Unheard : SignalState.Pending;
        if (next == State)
            return false;
        State = next;
        return true;
    }
}
