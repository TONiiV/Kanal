using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Kanal.Core.Diagnostics;

namespace Kanal.Core.Meetings;

public sealed class MeetingTitling(Func<IMeetingTitler?> titler, int minimumLines = 6)
{
    private CancellationTokenSource? _running;
    private int _generation;
    private bool _asked;

    public string? Title { get; private set; }

    public bool NamedByHand { get; private set; }

    public bool IsSuggesting { get; private set; }

    public bool Failed { get; private set; }

    public bool CanSuggest => titler() is not null;

    public event Action? Changed;

    public void Reset()
    {
        Cancel();
        _generation++;
        _asked = false;
        Title = null;
        NamedByHand = false;
        Failed = false;
        Changed?.Invoke();
    }

    public void Rename(string title)
    {
        var trimmed = title.Trim();
        if (trimmed.Length == 0) return;

        _generation++;
        Title = trimmed;
        NamedByHand = true;
        Failed = false;
        Changed?.Invoke();
    }

    public Task OfferAsync(IReadOnlyList<string> lines) =>
        NamedByHand || _asked || lines.Count < minimumLines
            ? Task.CompletedTask
            : SuggestAsync(lines);

    public Task RegenerateAsync(IReadOnlyList<string> lines) => SuggestAsync(lines);

    public void Cancel() => _running?.Cancel();

    private async Task SuggestAsync(IReadOnlyList<string> lines)
    {
        if (titler() is not { } model) return;

        _asked = true;
        var mine = ++_generation;
        var cts = new CancellationTokenSource();
        _running = cts;
        IsSuggesting = true;
        Failed = false;
        Changed?.Invoke();

        string? suggested = null;
        try
        {
            // No ConfigureAwait(false): everything this publishes is bound, so the completion
            // has to come back on the caller's context rather than a pool thread.
            suggested = await model.SuggestAsync(lines, cts.Token);
        }
        catch (Exception ex)
        {
            Log.Warning("titling", $"The meeting could not be named: {ex.Message}");
        }

        // Dropped rather than applied late: the operator renamed while this was in flight.
        var stale = mine != _generation;
        if (!stale && suggested?.Trim() is { Length: > 0 } title)
        {
            Title = title;
            NamedByHand = false;
        }
        else if (!stale)
        {
            Failed = true;
        }

        IsSuggesting = false;
        cts.Dispose();
        if (ReferenceEquals(_running, cts)) _running = null;
        Changed?.Invoke();
    }
}
