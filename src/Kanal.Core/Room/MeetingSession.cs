using Kanal.Core.Models;
using Kanal.Core.Providers;
using Kanal.Core.Relay;

namespace Kanal.Core.Room;

/// <summary>
/// Orchestrator: pumps ASR events into <see cref="RoomState"/>, publishes to the relay,
/// and — the one capability decision — routes finals through <see cref="IMtProvider"/>
/// when the ASR provider does not translate itself.
/// </summary>
public sealed class MeetingSession : IAsyncDisposable
{
    private readonly IAsrProvider _asr;
    private readonly IMtProvider? _mt;
    private readonly IRelayPublisher _relay;
    private readonly CancellationTokenSource _cts = new();
    private readonly List<Task> _pendingTranslations = new();
    private readonly object _gate = new();
    private readonly TimeSpan _translationGrace;
    private IAsrSession? _session;
    private Task? _pump;
    private int _disposed;
    private int _paused;

    /// <summary>How long shutdown waits for translations already in flight before cancelling them.</summary>
    public static readonly TimeSpan DefaultTranslationGrace = TimeSpan.FromSeconds(2);

    public MeetingSession(
        IAsrProvider asr,
        IMtProvider? mt,
        IRelayPublisher relay,
        RoomConfig config,
        TimeSpan? translationGrace = null)
    {
        _asr = asr;
        _mt = mt;
        _relay = relay;
        _translationGrace = translationGrace ?? DefaultTranslationGrace;
        Room = new RoomState(config);

        if (!asr.Caps.Translation && mt is null)
            throw new ArgumentException(
                $"ASR provider '{asr.Id}' does not translate and no IMtProvider was supplied.");
    }

    public RoomState Room { get; }

    public event Action<AsrEvent.Error>? ErrorOccurred;
    public event Action<string?>? SessionEnded;

    public async Task StartAsync(CancellationToken ct = default)
    {
        if (_session is not null)
            throw new InvalidOperationException("Session already started.");

        var config = Room.Config;
        _session = await _asr.StartAsync(
            new AsrSessionOptions(16_000, config.Languages, config.Languages), ct);
        _pump = Task.Run(() => PumpAsync(_cts.Token), CancellationToken.None);

        await _relay.PublishAsync(new RoomConfigMessage(config), ct);
    }

    /// <summary>
    /// True while the room is off the record. Pause is a privacy control before it is a
    /// convenience one — the operator steps out of a negotiation to talk to their own side —
    /// so it stops the audio at the door rather than hiding the transcript afterwards.
    /// </summary>
    public bool IsPaused => Volatile.Read(ref _paused) == 1;

    /// <summary>
    /// Takes the room off the record, or puts it back on. Announced to clients, since a column
    /// that simply stops looks exactly like a connection that broke. A no-op when the state is
    /// already what was asked for, so a repeated press does not fill the channel.
    /// </summary>
    public async Task SetPausedAsync(bool paused, CancellationToken ct = default)
    {
        if (Interlocked.Exchange(ref _paused, paused ? 1 : 0) == (paused ? 1 : 0))
            return;

        await PublishSafeAsync(new RoomPausedMessage(paused));
    }

    public ValueTask PushAudioAsync(ReadOnlyMemory<byte> pcm16, CancellationToken ct = default)
    {
        var session = _session ?? throw new InvalidOperationException("Session not started.");

        // Dropping the transcript but still streaming the room to a cloud transcriber would
        // mean the private conversation left the building and only the record of it was
        // hidden — worse than offering no pause at all.
        if (IsPaused)
            return ValueTask.CompletedTask;

        return session.PushAudioAsync(pcm16, ct);
    }

    public Speaker RenameSpeaker(string tag, string? displayName)
    {
        var speaker = Room.RenameSpeaker(tag, displayName);
        _ = PublishSafeAsync(new SpeakerUpsert(speaker));
        return speaker;
    }

    public Speaker MergeSpeakers(string fromTag, string intoTag)
    {
        var speaker = Room.MergeSpeakers(fromTag, intoTag);
        _ = PublishSafeAsync(new SpeakerUpsert(speaker));
        return speaker;
    }

    /// <summary>Publish the full state — served on late join and client reconnect.</summary>
    public Task PublishSnapshotAsync(CancellationToken ct = default) =>
        _relay.PublishAsync(new RoomSnapshotMessage(Room.Snapshot() with { Paused = IsPaused }), ct);

    /// <summary>Announce that this room is over, so clients stop presenting themselves as live.</summary>
    public Task PublishClosedAsync(CancellationToken ct = default) =>
        _relay.PublishAsync(new RoomClosedMessage(), ct);

    private async Task PumpAsync(CancellationToken ct)
    {
        var session = _session!;
        try
        {
            await foreach (var e in session.Events.WithCancellation(ct))
            {
                switch (e)
                {
                    case AsrEvent.Transcript t:
                        // A provider that generates its own audio (the scripted one) keeps
                        // talking through a pause; nothing it says while paused is recorded.
                        if (IsPaused)
                            break;

                        var utterance = Room.ApplyTranscript(t);
                        await PublishSafeAsync(new UtteranceUpsert(utterance));
                        if (t.IsFinal && !_asr.Caps.Translation && _mt is not null)
                            TrackTranslation(() => TranslateAsync(utterance, ct));
                        break;

                    case AsrEvent.Error error:
                        ErrorOccurred?.Invoke(error);
                        break;

                    case AsrEvent.Ended ended:
                        SessionEnded?.Invoke(ended.Reason);
                        return;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // normal shutdown
        }
        catch (Exception ex)
        {
            ErrorOccurred?.Invoke(new AsrEvent.Error(ex.Message, Fatal: true));
            SessionEnded?.Invoke(ex.Message);
        }
    }

    private async Task TranslateAsync(Utterance utterance, CancellationToken ct)
    {
        try
        {
            var targets = Room.Config.Languages
                .Where(l => !string.Equals(l, utterance.SrcLang, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (targets.Count == 0)
                return;

            var context = Room.RecentFinals(8, excludeId: utterance.Id);
            var translations = await _mt!.TranslateAsync(
                utterance.SrcText, utterance.SrcLang, targets, context, ct);

            var updated = Room.ApplyTranslations(utterance.Id, utterance.Revision, translations);
            if (updated is not null)
                await PublishSafeAsync(new TranslationUpsert(utterance.Id, utterance.Revision, translations));
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            ErrorOccurred?.Invoke(new AsrEvent.Error($"Translation failed: {ex.Message}", Fatal: false));
        }
    }

    /// <summary>
    /// Registers a translation as pending <em>before</em> starting it. Handing
    /// <c>TranslateAsync(…)</c> straight to a tracking method looked equivalent and was not: the
    /// call runs synchronously into the provider before the returned task is ever added to the
    /// list, so a shutdown landing in that window saw no pending work and dropped a translation
    /// that had in fact already begun.
    /// </summary>
    private void TrackTranslation(Func<Task> work)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate)
        {
            _pendingTranslations.RemoveAll(t => t.IsCompleted);
            _pendingTranslations.Add(completion.Task);
        }

        _ = RunAsync();
        return;

        async Task RunAsync()
        {
            try
            {
                await work();
            }
            catch
            {
                // TranslateAsync already reports through ErrorOccurred; nothing observes this
                // task, so a fault escaping here would surface as an unobserved exception
            }
            finally
            {
                completion.TrySetResult();
            }
        }
    }

    private async Task PublishSafeAsync(RelayMessage message)
    {
        try
        {
            await _relay.PublishAsync(message, _cts.Token);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            ErrorOccurred?.Invoke(new AsrEvent.Error($"Relay publish failed: {ex.Message}", Fatal: false));
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
            return;

        // stop the event source first, then give in-flight translations a bounded moment to
        // land, then cancel — which both unblocks whatever is still decoding and guarantees
        // the pump exits
        if (_session is not null)
            await _session.DisposeAsync();

        Task[] pending;
        lock (_gate)
        {
            pending = _pendingTranslations.ToArray();
        }

        var landed = Task.WhenAll(pending);
        try
        {
            // The grace is for a translation that is nearly done. Waiting on it unconditionally
            // — which is what this did — hands the operator's Stop button to the translator:
            // one local decode is seconds, and a model that spends its whole token budget
            // reasoning held Stop for twenty of them with the window frozen.
            if (_translationGrace > TimeSpan.Zero)
                await landed.WaitAsync(_translationGrace);
        }
        catch (TimeoutException)
        {
        }
        catch
        {
            // translation failures already surfaced via ErrorOccurred
        }

        _cts.Cancel();

        try
        {
            await landed; // cancelled ones unwind here; nothing is left running behind us
        }
        catch
        {
        }

        if (_pump is not null)
            await _pump;
        _cts.Dispose();
    }
}
