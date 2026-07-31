using Kanal.Core.Models;
using Kanal.Providers.LocalMt;

namespace Kanal.Tests;

public class MtPromptTests
{
    [Theory]
    [InlineData("de", "German")]
    [InlineData("pl", "Polish")]
    [InlineData("zh", "Chinese")]
    [InlineData("en", "English")]
    public void UsesEnglishLanguageName(string code, string name)
    {
        var prompt = MtPrompt.Build("这批支架的料号是 KX-4402。", code);
        Assert.Contains($"Translate the following into {name}.", prompt);
    }

    [Fact]
    public void ContainsInstructionAndSourceText()
    {
        var prompt = MtPrompt.Build("Wir brauchen das Erstmusterprüfprotokoll für KX-4402.", "pl");
        Assert.Contains("Output ONLY the translation, no explanations.", prompt);
        Assert.Contains("Keep part numbers, standards and units exactly as written.", prompt);
        Assert.EndsWith("Wir brauchen das Erstmusterprüfprotokoll für KX-4402.", prompt);
    }

    [Fact]
    public void UnknownCodeFallsBackToCodeItself()
    {
        Assert.Contains("Translate the following into xx.", MtPrompt.Build("t", "xx"));
    }
}

public class MtOutputCleanerTests
{
    [Fact]
    public void PassesPlainTranslationThrough()
    {
        Assert.Equal(
            "Numer katalogowy tych wsporników to KX-4402.",
            MtOutputCleaner.Clean("Numer katalogowy tych wsporników to KX-4402.\n"));
    }

    [Fact]
    public void StripsThinkBlocks()
    {
        var raw = "<think>\nThe user wants Polish. 支架 means bracket.\n</think>\n\nCzy próbki będą zgodne z normą ISO 7599?";
        Assert.Equal("Czy próbki będą zgodne z normą ISO 7599?", MtOutputCleaner.Clean(raw));
    }

    [Fact]
    public void UnterminatedThinkBlockYieldsEmpty()
    {
        Assert.Equal("", MtOutputCleaner.Clean("<think>\nstill reasoning about brackets"));
    }

    [Theory]
    [InlineData("\"Die Halterungen sind bestätigt.\"")]
    [InlineData("„Die Halterungen sind bestätigt.“")]
    [InlineData("«Die Halterungen sind bestätigt.»")]
    [InlineData("「Die Halterungen sind bestätigt.」")]
    public void StripsWrappingQuotes(string raw)
    {
        Assert.Equal("Die Halterungen sind bestätigt.", MtOutputCleaner.Clean(raw));
    }

    [Fact]
    public void KeepsInnerQuotesIntact()
    {
        var text = "Der Standard heißt \"ISO 7599\" und bleibt gleich.";
        Assert.Equal(text, MtOutputCleaner.Clean(text));
    }

    [Theory]
    [InlineData("Translation: Musimy potwierdzić termin dostawy.")]
    [InlineData("translation： Musimy potwierdzić termin dostawy.")]
    [InlineData("Übersetzung: Musimy potwierdzić termin dostawy.")]
    [InlineData("翻译： Musimy potwierdzić termin dostawy.")]
    public void StripsTranslationLabels(string raw)
    {
        Assert.Equal("Musimy potwierdzić termin dostawy.", MtOutputCleaner.Clean(raw));
    }

    [Fact]
    public void PreservesPartNumbersExactly()
    {
        var raw = "<think>ok</think>\n\"Die Teilenummer dieser Halterungen ist KX-4402, Oberflächenbehandlung nach ISO 7599.\"";
        var cleaned = MtOutputCleaner.Clean(raw);
        Assert.Contains("KX-4402", cleaned);
        Assert.Contains("ISO 7599", cleaned);
    }

    [Fact]
    public void EmptyAndWhitespaceYieldEmpty()
    {
        Assert.Equal("", MtOutputCleaner.Clean(""));
        Assert.Equal("", MtOutputCleaner.Clean("  \n "));
    }
}

public class LlamaSharpTextGeneratorTests
{
    /// <summary>
    /// Stands in for llama.cpp. <see cref="Dispose"/> records whether it was called while a
    /// decode was running — in the real backend that is a native use-after-free, which arrives
    /// as an AccessViolationException and takes the process down with the transcript unexported.
    /// </summary>
    private sealed class FakeBackend : ILlamaBackend
    {
        private readonly TaskCompletionSource _started =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _resume =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        private int _inFlight;

        public Task Started => _started.Task;
        public int Loads;
        public string? LoadedPath;
        public bool Freed;
        public bool FreedUnderARunningDecode;

        public void Resume() => _resume.TrySetResult();

        public Task LoadAsync(string modelPath, CancellationToken ct)
        {
            Interlocked.Increment(ref Loads);
            LoadedPath = modelPath;
            return Task.CompletedTask;
        }

        public async Task<string> InferAsync(string prompt, CancellationToken ct)
        {
            Interlocked.Increment(ref _inFlight);
            try
            {
                _started.TrySetResult();
                await _resume.Task.WaitAsync(ct);
                return "Wsporniki KX-4402.";
            }
            finally
            {
                Interlocked.Decrement(ref _inFlight);
            }
        }

        public void Dispose()
        {
            if (Volatile.Read(ref _inFlight) > 0)
                FreedUnderARunningDecode = true;
            Freed = true;
        }
    }

    /// <summary>
    /// A final tracked after MeetingSession snapshotted its pending translations can still be
    /// decoding when Stop disposes the provider. Disposal has to wait for it: freeing the
    /// weights under a live decode is a use-after-free, and tearing down the gate under a
    /// parked caller surfaces to the operator as "Translation failed".
    /// </summary>
    [Fact]
    public async Task DisposeWaitsForAnInFlightGeneration()
    {
        var backend = new FakeBackend();
        var generator = new LlamaSharpTextGenerator("qwen.gguf", backend);

        var generating = generator.GenerateAsync("prompt", CancellationToken.None);
        await backend.Started.WaitAsync(TimeSpan.FromSeconds(10));

        var disposing = Task.Run(generator.Dispose);

        var finishedEarly = await Task.WhenAny(disposing, Task.Delay(250)) == disposing;
        Assert.False(finishedEarly, "Dispose returned while a decode was still running.");
        Assert.False(backend.FreedUnderARunningDecode, "the weights were freed under a live decode.");
        Assert.False(backend.Freed);

        backend.Resume();

        // the decode completes normally — no ObjectDisposedException out of the released gate
        Assert.Equal("Wsporniki KX-4402.", await generating.WaitAsync(TimeSpan.FromSeconds(10)));
        await disposing.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(backend.Freed);
        Assert.False(backend.FreedUnderARunningDecode);
    }

    [Fact]
    public async Task GenerateAfterDisposeThrowsObjectDisposed()
    {
        var backend = new FakeBackend();
        backend.Resume();
        var generator = new LlamaSharpTextGenerator("qwen.gguf", backend);
        await generator.GenerateAsync("prompt", CancellationToken.None);

        generator.Dispose();

        await Assert.ThrowsAsync<ObjectDisposedException>(() =>
            generator.GenerateAsync("prompt", CancellationToken.None));
    }

    /// <summary>The path Stop actually takes — same guarantee, without blocking the UI thread.</summary>
    [Fact]
    public async Task DisposeAsyncWaitsForAnInFlightGeneration()
    {
        var backend = new FakeBackend();
        var provider = new LlamaSharpMtProvider(new LlamaSharpTextGenerator("qwen.gguf", backend));

        var translating = provider.TranslateAsync(
            "这批支架。", "zh", ["pl"], Array.Empty<Utterance>(), CancellationToken.None);
        await backend.Started.WaitAsync(TimeSpan.FromSeconds(10));

        var disposing = provider.DisposeAsync().AsTask();
        Assert.False(await Task.WhenAny(disposing, Task.Delay(250)) == disposing);
        Assert.False(backend.FreedUnderARunningDecode);

        backend.Resume();
        var result = await translating.WaitAsync(TimeSpan.FromSeconds(10));
        await disposing.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal("Wsporniki KX-4402.", result["pl"]);
        Assert.True(backend.Freed);
        Assert.False(backend.FreedUnderARunningDecode);
    }

    [Fact]
    public void DisposeIsIdempotent()
    {
        var backend = new FakeBackend();
        var generator = new LlamaSharpTextGenerator("qwen.gguf", backend);

        generator.Dispose();
        generator.Dispose();

        Assert.True(backend.Freed);
    }

    [Fact]
    public async Task WeightsLoadOnceLazilyAndFromTheGivenPath()
    {
        var backend = new FakeBackend();
        backend.Resume();
        var generator = new LlamaSharpTextGenerator("qwen.gguf", backend);
        Assert.Equal(0, backend.Loads); // constructing must not block on a multi-gigabyte load

        await generator.GenerateAsync("a", CancellationToken.None);
        await generator.GenerateAsync("b", CancellationToken.None);

        Assert.Equal(1, backend.Loads);
        Assert.Equal("qwen.gguf", backend.LoadedPath);
        generator.Dispose();
    }
}

public class LlamaSharpMtProviderTests
{
    private sealed class FakeTextGenerator : ITextGenerator
    {
        public List<string> Prompts { get; } = new();
        public Func<string, string> Respond { get; set; } = p => "out";

        public Task<string> GenerateAsync(string prompt, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Prompts.Add(prompt);
            return Task.FromResult(Respond(prompt));
        }
    }

    private static readonly IReadOnlyList<Utterance> NoContext = Array.Empty<Utterance>();

    [Fact]
    public async Task TranslatesIntoEveryTargetLanguage()
    {
        var generator = new FakeTextGenerator
        {
            Respond = p => p.Contains("German") ? "Die Halterungen." : "Wsporniki.",
        };
        var provider = new LlamaSharpMtProvider(generator);

        var result = await provider.TranslateAsync(
            "这批支架。", "zh", ["de", "pl"], NoContext, CancellationToken.None);

        Assert.Equal(2, result.Count);
        Assert.Equal("Die Halterungen.", result["de"]);
        Assert.Equal("Wsporniki.", result["pl"]);
        Assert.Equal(2, generator.Prompts.Count);
    }

    [Fact]
    public async Task SkipsSourceLanguageTarget()
    {
        var generator = new FakeTextGenerator();
        var provider = new LlamaSharpMtProvider(generator);

        var result = await provider.TranslateAsync(
            "text", "zh", ["zh", "de"], NoContext, CancellationToken.None);

        Assert.Single(result);
        Assert.True(result.ContainsKey("de"));
        Assert.Single(generator.Prompts);
    }

    [Fact]
    public async Task CleansGeneratorOutput()
    {
        var generator = new FakeTextGenerator
        {
            Respond = _ => "<think>reasoning</think>\n\"Wsporniki KX-4402.\"",
        };
        var provider = new LlamaSharpMtProvider(generator);

        var result = await provider.TranslateAsync(
            "支架 KX-4402。", "zh", ["pl"], NoContext, CancellationToken.None);

        Assert.Equal("Wsporniki KX-4402.", result["pl"]);
    }

    [Fact]
    public async Task OmitsTargetWhenOutputCleansToEmpty()
    {
        var generator = new FakeTextGenerator
        {
            Respond = p => p.Contains("German") ? "<think>never stops" : "Wsporniki.",
        };
        var provider = new LlamaSharpMtProvider(generator);

        var result = await provider.TranslateAsync(
            "支架。", "zh", ["de", "pl"], NoContext, CancellationToken.None);

        Assert.Single(result);
        Assert.Equal("Wsporniki.", result["pl"]);
    }

    [Fact]
    public async Task CancellationStopsTheLoop()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var provider = new LlamaSharpMtProvider(new FakeTextGenerator());

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            provider.TranslateAsync("text", "zh", ["de"], NoContext, cts.Token));
    }

    [Fact]
    public void HasStableId()
    {
        Assert.Equal("local-llm", new LlamaSharpMtProvider(new FakeTextGenerator()).Id);
    }
}
