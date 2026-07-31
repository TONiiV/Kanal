namespace Kanal.Providers.LocalMt;

/// <summary>
/// Real inference through LLamaSharp (llama.cpp in-process). Weights load lazily
/// on the first request so constructing the provider never blocks the UI thread;
/// requests are serialized because one local model context serves them all.
/// Deliberately thin: everything testable lives in front of <see cref="ITextGenerator"/>.
/// </summary>
public sealed class LlamaSharpTextGenerator : ITextGenerator, IDisposable, IAsyncDisposable
{
    private readonly string _modelPath;
    private readonly ILlamaBackend _backend;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _loaded;
    private bool _disposed;

    public LlamaSharpTextGenerator(string modelPath)
        : this(modelPath, new LlamaCppBackend())
    {
    }

    /// <summary>Test seam: a fake backend stands in for llama.cpp and the model file.</summary>
    public LlamaSharpTextGenerator(string modelPath, ILlamaBackend backend)
    {
        _modelPath = modelPath;
        _backend = backend;
    }

    public async Task<string> GenerateAsync(string prompt, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_loaded)
            {
                await _backend.LoadAsync(_modelPath, ct);
                _loaded = true;
            }

            return await _backend.InferAsync(prompt, ct);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Frees the weights, waiting for an in-flight decode first. Stop can reach here while a
    /// final tracked after the session snapshotted its pending translations is still decoding,
    /// and llama.cpp weights are freed natively: releasing them under a live decode is a
    /// use-after-free that ends the process, not an exception the host could report.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync();
        try
        {
            DisposeUnderGate();
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc cref="DisposeAsync"/>
    /// <remarks>Blocks the calling thread for the rest of the decode; prefer
    /// <see cref="DisposeAsync"/> anywhere the UI thread is involved.</remarks>
    public void Dispose()
    {
        _gate.Wait();
        try
        {
            DisposeUnderGate();
        }
        finally
        {
            _gate.Release();
        }
    }

    private void DisposeUnderGate()
    {
        if (_disposed)
            return;
        _disposed = true;
        _backend.Dispose();

        // _gate is deliberately not disposed. A caller parked in WaitAsync has to resume into
        // the ObjectDisposedException above, not into a disposed semaphore inside Release() —
        // which reaches the operator as "Translation failed" instead of a clear shutdown. A
        // SemaphoreSlim whose AvailableWaitHandle was never touched holds nothing to release.
    }
}
