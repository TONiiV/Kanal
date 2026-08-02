using System.Text;
using LLama;
using LLama.Common;
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
    private ModelParams? _params;
    private LLamaWeights? _weights;
    private string? _assistantPrefill;

    public async Task LoadAsync(string modelPath, string? assistantPrefill, CancellationToken ct)
    {
        _assistantPrefill = assistantPrefill;
        _params = new ModelParams(modelPath)
        {
            ContextSize = 4096,
            GpuLayerCount = 999, // offload everything the backend supports (Metal on mac)
        };
        _weights = await LLamaWeights.LoadFromFileAsync(_params, ct);
    }

    public async Task<string> InferAsync(string prompt, CancellationToken ct)
    {
        var executor = new StatelessExecutor(_weights!, _params!);
        var inference = new InferenceParams
        {
            MaxTokens = 512,
            SamplingPipeline = new DefaultSamplingPipeline { Temperature = 0.2f },
        };

        var output = new StringBuilder();
        await foreach (var piece in executor.InferAsync(ApplyChatTemplate(prompt), inference, ct))
            output.Append(piece);
        return output.ToString();
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
        _weights?.Dispose();
        _weights = null;
    }
}
