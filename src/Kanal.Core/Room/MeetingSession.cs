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
    private IAsrSession? _session;
    private Task? _pump;
    private int _disposed;

    public MeetingSession(IAsrProvider asr, IMtProvider? mt, IRelayPublisher relay, RoomConfig config)
    {
        _asr = asr;
        _mt = mt;
        _relay = relay;
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

    public ValueTask PushAudioAsync(ReadOnlyMemory<byte> pcm16, CancellationToken ct = default)
    {
        var session = _session ?? throw new InvalidOperationException("Session not started.");
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
        _relay.PublishAsync(new RoomSnapshotMessage(Room.Snapshot()), ct);

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
                        var utterance = Room.ApplyTranscript(t);
                        await PublishSafeAsync(new UtteranceUpsert(utterance));
                        if (t.IsFinal && !_asr.Caps.Translation && _mt is not null)
                            TrackTranslation(TranslateAsync(utterance, ct));
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

    private void TrackTranslation(Task task)
    {
        lock (_gate)
        {
            _pendingTranslations.RemoveAll(t => t.IsCompleted);
            _pendingTranslations.Add(task);
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

        // stop the event source first, then let in-flight translations land,
        // then cancel as a backstop so the pump is guaranteed to exit
        if (_session is not null)
            await _session.DisposeAsync();

        Task[] pending;
        lock (_gate)
        {
            pending = _pendingTranslations.ToArray();
        }

        try
        {
            await Task.WhenAll(pending);
        }
        catch
        {
            // translation failures already surfaced via ErrorOccurred
        }

        _cts.Cancel();
        if (_pump is not null)
            await _pump;
        _cts.Dispose();
    }
}
