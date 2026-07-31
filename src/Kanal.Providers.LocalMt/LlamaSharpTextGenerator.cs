using System.Text;
using LLama;
using LLama.Common;
using LLama.Sampling;

namespace Kanal.Providers.LocalMt;

/// <summary>
/// Real inference through LLamaSharp (llama.cpp in-process). Weights load lazily
/// on the first request so constructing the provider never blocks the UI thread;
/// requests are serialized because one local model context serves them all.
/// Deliberately thin: everything testable lives in front of <see cref="ITextGenerator"/>.
/// </summary>
public sealed class LlamaSharpTextGenerator : ITextGenerator, IDisposable
{
    private readonly string _modelPath;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private ModelParams? _params;
    private LLamaWeights? _weights;
    private bool _disposed;

    public LlamaSharpTextGenerator(string modelPath) => _modelPath = modelPath;

    public async Task<string> GenerateAsync(string prompt, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_weights is null)
            {
                _params = new ModelParams(_modelPath)
                {
                    ContextSize = 4096,
                    GpuLayerCount = 999, // offload everything the backend supports (Metal on mac)
                };
                _weights = await LLamaWeights.LoadFromFileAsync(_params, ct);
            }

            var executor = new StatelessExecutor(_weights, _params!);
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
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Wrap the prompt in the model's own chat template (ChatML for Qwen, Gemma format for Gemma…).</summary>
    private string ApplyChatTemplate(string userMessage)
    {
        var template = new LLamaTemplate(_weights!) { AddAssistant = true };
        template.Add("user", userMessage);
        return Encoding.UTF8.GetString(template.Apply());
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _weights?.Dispose();
        _gate.Dispose();
    }
}
