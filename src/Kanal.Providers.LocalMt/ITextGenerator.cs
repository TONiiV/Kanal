namespace Kanal.Providers.LocalMt;

/// <summary>
/// The single seam between translation logic and actual LLM inference.
/// Production uses <see cref="LlamaSharpTextGenerator"/>; tests use fakes so
/// prompt construction and output cleaning stay verifiable without a model.
/// </summary>
public interface ITextGenerator
{
    /// <summary>Run one completion for <paramref name="prompt"/> and return the raw model output.</summary>
    Task<string> GenerateAsync(string prompt, CancellationToken ct);
}
