using Kanal.Core.Models;
using Kanal.Core.Providers;

namespace Kanal.Providers.LocalMt;

/// <summary>
/// In-process translation provider: one prompt per target language through an
/// <see cref="ITextGenerator"/>. Sequential on purpose — a local model shares
/// one context and gains nothing from parallel requests.
/// </summary>
public sealed class LlamaSharpMtProvider : IMtProvider, IDisposable
{
    private readonly ITextGenerator _generator;

    public LlamaSharpMtProvider(ITextGenerator generator) => _generator = generator;

    public string Id => "local-llm";

    public async Task<IReadOnlyDictionary<string, string>> TranslateAsync(
        string text,
        string from,
        IReadOnlyList<string> to,
        IReadOnlyList<Utterance> context,
        CancellationToken ct)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var target in to)
        {
            if (string.Equals(target, from, StringComparison.OrdinalIgnoreCase))
                continue;

            ct.ThrowIfCancellationRequested();
            var raw = await _generator.GenerateAsync(MtPrompt.Build(text, target), ct);
            var cleaned = MtOutputCleaner.Clean(raw);
            if (cleaned.Length > 0)
                result[target] = cleaned;
        }

        return result;
    }

    public void Dispose() => (_generator as IDisposable)?.Dispose();
}
