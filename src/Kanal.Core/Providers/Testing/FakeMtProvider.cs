using Kanal.Core.Models;

namespace Kanal.Core.Providers.Testing;

/// <summary>
/// Placeholder translator: tags text with the target language so the
/// decoupled ASR→MT path is visible end to end without a real model.
/// </summary>
public sealed class FakeMtProvider : IMtProvider
{
    private readonly TimeSpan _delay;

    public FakeMtProvider(TimeSpan? delay = null) => _delay = delay ?? TimeSpan.FromMilliseconds(200);

    public string Id => "fake-mt";

    public async Task<IReadOnlyDictionary<string, string>> TranslateAsync(
        string text, string from, IReadOnlyList<string> to,
        IReadOnlyList<Utterance> context, CancellationToken ct)
    {
        await Task.Delay(_delay, ct);
        return to.ToDictionary(lang => lang, lang => $"[{from}→{lang}] {text}");
    }
}
