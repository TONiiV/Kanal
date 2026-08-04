using Kanal.Core.Providers;
using Kanal.Providers.LocalMt;

namespace Kanal.Core.UnitTests;

/// <summary>
/// Warm-up is the explicit "load the weights now" entry point. Without it the first final of
/// the meeting paid the whole multi-gigabyte load: the operator pressed Start, the room spoke,
/// and the opening sentences sat untranslated for as long as llama.cpp took to map the model.
/// Warm-up must be idempotent (Start retries, refreshes), cancellable (the operator changes
/// their mind mid-load), and must leave the lazy path intact for anything that never warms up.
/// </summary>
public class LlamaSharpWarmupTests
{
    private sealed class CountingBackend : ILlamaBackend
    {
        public int Loads;
        public Task Gate = Task.CompletedTask;

        public async Task LoadAsync(string modelPath, string? assistantPrefill, CancellationToken ct)
        {
            Interlocked.Increment(ref Loads);
            await Gate.WaitAsync(ct);
        }

        public Task<string> InferAsync(string prompt, CancellationToken ct) =>
            Task.FromResult("Wsporniki KX-4402.");

        public void Dispose()
        {
        }
    }

    [Fact]
    public async Task WarmUpLoadsTheWeightsExactlyOnce()
    {
        var backend = new CountingBackend();
        var generator = new LlamaSharpTextGenerator("qwen.gguf", backend);

        await ((IWarmupProvider)generator).WarmUpAsync(CancellationToken.None);
        await ((IWarmupProvider)generator).WarmUpAsync(CancellationToken.None);

        Assert.Equal(1, backend.Loads);
    }

    [Fact]
    public async Task GenerateAfterWarmUpDoesNotLoadAgain()
    {
        var backend = new CountingBackend();
        var generator = new LlamaSharpTextGenerator("qwen.gguf", backend);

        await ((IWarmupProvider)generator).WarmUpAsync(CancellationToken.None);
        await generator.GenerateAsync("prompt", CancellationToken.None);

        Assert.Equal(1, backend.Loads);
    }

    /// <summary>A cancelled load is abandoned, not latched: the next warm-up loads again.</summary>
    [Fact]
    public async Task CancelledWarmUpUnwindsAndTheNextOneRetries()
    {
        var backend = new CountingBackend();
        var never = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        backend.Gate = never.Task;
        var generator = new LlamaSharpTextGenerator("qwen.gguf", backend);

        using var cts = new CancellationTokenSource();
        var warming = ((IWarmupProvider)generator).WarmUpAsync(cts.Token);
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => warming);

        backend.Gate = Task.CompletedTask;
        await ((IWarmupProvider)generator).WarmUpAsync(CancellationToken.None);
        Assert.Equal(2, backend.Loads);
    }

    [Fact]
    public async Task WarmUpAfterDisposeThrowsObjectDisposed()
    {
        var generator = new LlamaSharpTextGenerator("qwen.gguf", new CountingBackend());
        generator.Dispose();

        await Assert.ThrowsAsync<ObjectDisposedException>(() =>
            ((IWarmupProvider)generator).WarmUpAsync(CancellationToken.None));
    }

    [Fact]
    public async Task ProviderWarmUpReachesTheBackend()
    {
        var backend = new CountingBackend();
        var provider = new LlamaSharpMtProvider(new LlamaSharpTextGenerator("qwen.gguf", backend));

        await ((IWarmupProvider)provider).WarmUpAsync(CancellationToken.None);

        Assert.Equal(1, backend.Loads);
    }

    private sealed class PlainGenerator : ITextGenerator
    {
        public Task<string> GenerateAsync(string prompt, CancellationToken ct) =>
            Task.FromResult("out");
    }

    /// <summary>A generator with nothing to preload makes warm-up a no-op, not a failure.</summary>
    [Fact]
    public async Task ProviderWithNothingToLoadWarmsUpAsANoOp()
    {
        var provider = new LlamaSharpMtProvider(new PlainGenerator());

        await ((IWarmupProvider)provider).WarmUpAsync(CancellationToken.None);
    }
}
