using Kanal.Core.Models;
using Kanal.Core.Providers;

namespace Kanal.Providers.LocalMt;

/// <summary>
/// In-process translation provider: one prompt per target language, all in one batch
/// through an <see cref="ITextGenerator"/>.
/// </summary>
public sealed class LlamaSharpMtProvider : IMtProvider, IWarmupProvider, IDisposable, IAsyncDisposable
{
    private readonly ITextGenerator _generator;

    public LlamaSharpMtProvider(ITextGenerator generator) => _generator = generator;

    public string Id => "local-llm";

    /// <summary>Loads the generator's weights ahead of the first translation. A generator with
    /// nothing to preload (a fake, a remote one) makes this a no-op, not a failure.</summary>
    public Task WarmUpAsync(CancellationToken ct) =>
        _generator is IWarmupProvider warmable ? warmable.WarmUpAsync(ct) : Task.CompletedTask;

    public async Task<IReadOnlyDictionary<string, string>> TranslateAsync(
        string text,
        string from,
        IReadOnlyList<string> to,
        IReadOnlyList<Utterance> context,
        CancellationToken ct)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var empty = new List<string>();
        var targets = to.Where(t => !string.Equals(t, from, StringComparison.OrdinalIgnoreCase)).ToList();
        if (targets.Count == 0)
            return result;

        ct.ThrowIfCancellationRequested();
        var raw = await _generator.GenerateAsync(targets.Select(t => MtPrompt.Build(text, t)).ToList(), ct);
        for (var i = 0; i < targets.Count; i++)
        {
            var cleaned = MtOutputCleaner.Clean(raw[i]);
            if (cleaned.Length > 0)
                result[targets[i]] = cleaned;
            else
                empty.Add(targets[i]);
        }

        // Nothing at all coming back is a broken translator, and it is indistinguishable on
        // screen from a slow one: every column waits on "…" for the rest of the meeting with no
        // message anywhere. Saying so costs one warning line; staying quiet cost a whole
        // rehearsal. A partial result is not raised — the languages that worked are worth more
        // than a warning about the one that did not.
        if (result.Count == 0 && empty.Count > 0)
            throw new InvalidOperationException(
                $"the local model returned no translation for {string.Join(", ", empty)}");

        return result;
    }

    /// <summary>Preferred over <see cref="Dispose"/>: the generator waits for an in-flight
    /// decode before freeing native weights, and Stop should not block the UI thread on it.</summary>
    public async ValueTask DisposeAsync()
    {
        switch (_generator)
        {
            case IAsyncDisposable async:
                await async.DisposeAsync();
                break;
            case IDisposable sync:
                sync.Dispose();
                break;
        }
    }

    public void Dispose() => (_generator as IDisposable)?.Dispose();
}
