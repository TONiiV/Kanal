using System.Text;
using Kanal.Core.Diagnostics;
using LLama;
using LLama.Batched;
using LLama.Common;
using LLama.Exceptions;
using LLama.Native;
using LLama.Sampling;

namespace Kanal.Providers.LocalMt;

// Unit tests cannot load llama.cpp. The benchmark (tests/Kanal.Benchmark) is the regression check for the sequence-id budget.
public sealed class LlamaCppBackend : ILlamaBackend
{
    private const int MaxTokens = 512;
    private const int SeqMax = 8;

    private ModelParams? _params;
    private LLamaWeights? _weights;
    private BatchedExecutor? _executor;
    private int _sequencesUsed;
    private string? _assistantPrefill;

    public async Task LoadAsync(string modelPath, string? assistantPrefill, CancellationToken ct)
    {
        _assistantPrefill = assistantPrefill;
        var accelerator = LlamaAcceleration.EnsureConfigured();
        Log.Info("LocalMt", $"llama.cpp runs on {accelerator}.");
        _params = new ModelParams(modelPath)
        {
            ContextSize = 4096,
            SeqMax = SeqMax,
            GpuLayerCount = accelerator == Accelerator.Cpu ? 0 : 999,
        };
        _weights = await LLamaWeights.LoadFromFileAsync(_params, ct);
    }

    public async Task<string> InferAsync(string prompt, CancellationToken ct) =>
        (await InferAsync([prompt], ct))[0];

    // One sequence per prompt in one shared context: a GPU decodes 3 targets in about the time of 1.
    public async Task<IReadOnlyList<string>> InferAsync(IReadOnlyList<string> prompts, CancellationToken ct)
    {
        var executor = ExecutorFor(prompts.Count);
        var runs = new List<Run>();

        try
        {
            foreach (var prompt in prompts)
            {
                var conversation = executor.Create();
                runs.Add(new Run(conversation, new DefaultSamplingPipeline { Temperature = 0.2f },
                    new StreamingTokenDecoder(executor.Context)));
                conversation.Prompt(executor.Context.Tokenize(ApplyChatTemplate(prompt), special: true));
            }

            while (runs.Any(r => !r.Done))
            {
                var decoded = await executor.Infer(ct);
                // ponytail: a full KV cache fails the sentence; 4096 tokens hold 3 targets of 512 with room to spare.
                if (decoded != DecodeResult.Ok)
                    throw new LLamaDecodeError(decoded);

                foreach (var run in runs.Where(r => !r.Done && r.Conversation.RequiresSampling))
                {
                    var token = run.Conversation.Sample(run.Sampler);
                    if (token.IsEndOfGeneration(_weights!.Vocab))
                    {
                        run.Done = true;
                        continue;
                    }

                    run.Decoder.Add(token);
                    if (++run.Tokens >= MaxTokens)
                    {
                        run.Done = true;
                        continue;
                    }

                    run.Conversation.Prompt(token);
                }
            }

            return runs.Select(r => r.Decoder.Read()).ToList();
        }
        catch
        {
            _sequencesUsed = SeqMax;
            throw;
        }
        finally
        {
            foreach (var run in runs)
            {
                run.Conversation.Dispose();
                run.Sampler.Dispose();
            }
        }
    }

    // BatchedExecutor never reuses a sequence id. llama.cpp rejects an id >= SeqMax for recurrent and
    // hybrid models (Qwen3.5), and an id >= 256 for the others. A new executor starts again at id 0.
    private BatchedExecutor ExecutorFor(int sequences)
    {
        if (_executor is null || _sequencesUsed + sequences > SeqMax)
        {
            _executor?.Dispose();
            _executor = new BatchedExecutor(_weights!, _params!);
            _sequencesUsed = 0;
        }

        _sequencesUsed += sequences;
        return _executor;
    }

    /// <summary>
    /// Wrap the prompt in the model's own chat template (ChatML for Qwen, Gemma format for
    /// Gemma…), then open the assistant turn with whatever the catalog says this model needs —
    /// for a reasoning model, an already-closed think block, so the token budget goes to the
    /// translation instead of to deliberation the operator never sees.
    /// </summary>
    private string ApplyChatTemplate(string userMessage)
    {
        var template = new LLamaTemplate(_weights!) { AddAssistant = true };
        template.Add("user", userMessage);
        return Encoding.UTF8.GetString(template.Apply()) + _assistantPrefill;
    }

    public void Dispose()
    {
        _executor?.Dispose();
        _executor = null;
        _weights?.Dispose();
        _weights = null;
    }

    private sealed class Run(Conversation conversation, DefaultSamplingPipeline sampler, StreamingTokenDecoder decoder)
    {
        public Conversation Conversation { get; } = conversation;
        public DefaultSamplingPipeline Sampler { get; } = sampler;
        public StreamingTokenDecoder Decoder { get; } = decoder;
        public int Tokens { get; set; }
        public bool Done { get; set; }
    }
}
