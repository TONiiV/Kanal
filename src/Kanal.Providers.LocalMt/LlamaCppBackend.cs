using System.Text;
using Kanal.Core.Diagnostics;
using LLama;
using LLama.Batched;
using LLama.Common;
using LLama.Exceptions;
using LLama.Native;
using LLama.Sampling;

namespace Kanal.Providers.LocalMt;

/// <summary>
/// The real llama.cpp backend. Deliberately thin and untested: it holds no rules of its
/// own, only the LLamaSharp calls. Everything with a decision in it — when weights load,
/// how calls are serialized, when the weights are freed — lives in
/// <see cref="LlamaSharpTextGenerator"/>, in front of <see cref="ILlamaBackend"/>.
/// </summary>
public sealed class LlamaCppBackend : ILlamaBackend
{
    private const int MaxTokens = 512;
    private const int SeqMax = 16;

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
        var runs = prompts.Select(prompt =>
        {
            var conversation = executor.Create();
            conversation.Prompt(executor.Context.Tokenize(ApplyChatTemplate(prompt), special: true));
            return new Run(conversation, new DefaultSamplingPipeline { Temperature = 0.2f },
                new StreamingTokenDecoder(executor.Context));
        }).ToList();

        try
        {
            while (runs.Any(r => !r.Done))
            {
                var decoded = await executor.Infer(ct);
                // ponytail: a full KV cache fails the sentence; 4096 tokens hold 3 targets of 512 with room to spare.
                if (decoded != DecodeResult.Ok)
                    throw new LLamaDecodeError(decoded);

                foreach (var run in runs.Where(r => !r.Done && r.Conversation.RequiresSampling))
                {
                    var token = run.Conversation.Sample(run.Sampler);
                    if (token.IsEndOfGeneration(_weights!.Vocab) || ++run.Tokens >= MaxTokens)
                    {
                        run.Done = true;
                        continue;
                    }
                    run.Decoder.Add(token);
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

    // BatchedExecutor never reuses a sequence id, and llama.cpp rejects an id at or above SeqMax
    // (hybrid models such as Qwen3.5 at once). A fresh executor starts again at id 0.
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
