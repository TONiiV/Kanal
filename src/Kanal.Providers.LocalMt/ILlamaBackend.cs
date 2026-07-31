namespace Kanal.Providers.LocalMt;

/// <summary>
/// The native half of local inference, split out so the lifetime rules around it —
/// load once, one call at a time, never free under a running decode — are testable
/// without llama.cpp or a multi-gigabyte model on disk.
/// </summary>
/// <remarks>
/// <see cref="IDisposable.Dispose"/> frees native memory. Calling it while
/// <see cref="InferAsync"/> is running is a use-after-free, not an exception, so
/// <see cref="LlamaSharpTextGenerator"/> serializes the two.
/// </remarks>
public interface ILlamaBackend : IDisposable
{
    Task LoadAsync(string modelPath, string? assistantPrefill, CancellationToken ct);

    Task<string> InferAsync(string prompt, CancellationToken ct);
}
