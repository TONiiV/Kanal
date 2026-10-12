using System.Threading.Channels;
using Kanal.Core.Diagnostics;
using Kanal.Core.Models;
using Kanal.Core.Room;

namespace Kanal.Core.Insights;

public sealed class MeetingAgentRunner : IAsyncDisposable
{
    private const string LogCategory = "agent";

    private readonly RoomState _room;
    private readonly IMeetingAgent _agent;
    private readonly Action<MeetingInsight> _propose;
    private readonly string _replyLanguage;
    private readonly int _batch;
    private readonly TimeSpan _quiet;
    private readonly Channel<Utterance> _finals = Channel.CreateUnbounded<Utterance>();
    private readonly HashSet<string> _seen = [];
    private readonly List<string> _topics = [];
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _loop;

    public MeetingAgentRunner(
        RoomState room,
        IMeetingAgent agent,
        Action<MeetingInsight> propose,
        string replyLanguage,
        int batch = 8,
        TimeSpan? quiet = null)
    {
        _room = room;
        _agent = agent;
        _propose = propose;
        _replyLanguage = replyLanguage;
        _batch = batch;
        _quiet = quiet ?? TimeSpan.FromSeconds(10);
        _room.UtteranceUpserted += OnUpserted;
        _loop = Task.Run(() => RunAsync(_stop.Token));
    }

    // Raised on the session's own threads: this must never wait, or the agent slows the transcript.
    private void OnUpserted(Utterance utterance)
    {
        if (utterance.State != UtteranceState.Final)
            return;
        lock (_seen)
        {
            if (!_seen.Add(utterance.Id))
                return;
        }

        _finals.Writer.TryWrite(utterance);
    }

    private async Task RunAsync(CancellationToken ct)
    {
        var window = new List<Utterance>();
        try
        {
            while (true)
            {
                window.Add(await _finals.Reader.ReadAsync(ct));
                while (window.Count < _batch && await NextBeforeQuietAsync(ct) is { } next)
                    window.Add(next);

                await ProposeAsync([.. window], ct);
                window.Clear();
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
    }

    private async Task<Utterance?> NextBeforeQuietAsync(CancellationToken ct)
    {
        using var quiet = CancellationTokenSource.CreateLinkedTokenSource(ct);
        quiet.CancelAfter(_quiet);
        try
        {
            return await _finals.Reader.ReadAsync(quiet.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return null;
        }
    }

    private async Task ProposeAsync(IReadOnlyList<Utterance> finals, CancellationToken ct)
    {
        try
        {
            var found = await _agent.ProposeAsync(new MeetingAgentWindow(finals, [.. _topics], _replyLanguage), ct);
            ct.ThrowIfCancellationRequested();
            foreach (var insight in found.Where(i => _agent.Caps.Kinds.Contains(i.Kind)))
            {
                if (!_topics.Contains(insight.Topic))
                    _topics.Add(insight.Topic);
                _propose(insight with { Id = Guid.NewGuid().ToString("N"), State = InsightState.Candidate });
            }
        }
        catch (Exception) when (ct.IsCancellationRequested)
        {
            throw new OperationCanceledException(ct);
        }
        catch (Exception ex)
        {
            Log.Warning(LogCategory, $"Agent {_agent.Id} failed on {finals.Count} finals; they are skipped.", ex);
        }
    }

    public async ValueTask DisposeAsync()
    {
        _room.UtteranceUpserted -= OnUpserted;
        await _stop.CancelAsync();
        await _loop;
        _stop.Dispose();
    }
}
