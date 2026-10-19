using LLama.Native;

namespace Kanal.Providers.LocalMt;

public enum Accelerator
{
    Cpu,
    Metal,
    Cuda,
    Vulkan,
}

public static class LlamaAcceleration
{
    public const string OverrideVariable = "KANAL_ACCELERATOR";

    private static readonly Lock Gate = new();
    private static Accelerator? _active;

    // LLamaSharp already tries CUDA, then Vulkan, then CPU; only a forced value needs config calls.
    public static Accelerator EnsureConfigured()
    {
        lock (Gate)
        {
            if (_active is { } active)
                return active;

            var forced = Parse(Environment.GetEnvironmentVariable(OverrideVariable));
            if (forced is not null && !NativeLibraryConfig.LLama.LibraryHasLoaded)
                NativeLibraryConfig.All.WithCuda(forced == Accelerator.Cuda).WithVulkan(forced == Accelerator.Vulkan);

            NativeApi.llama_empty_call();
            var metadata = NativeApi.GetLoadedNativeLibrary(NativeLibraryName.LLama)?.Metadata;
            _active = Classify(metadata?.UseCuda == true, metadata?.UseVulkan == true,
                NativeApi.llama_supports_gpu_offload(), OperatingSystem.IsMacOS(), forced);
            return _active.Value;
        }
    }

    internal static Accelerator Classify(bool useCuda, bool useVulkan, bool gpuOffload, bool macOS, Accelerator? forced) =>
        forced == Accelerator.Cpu || !gpuOffload ? Accelerator.Cpu
        : useCuda ? Accelerator.Cuda
        : useVulkan ? Accelerator.Vulkan
        : macOS ? Accelerator.Metal
        : Accelerator.Cpu;

    internal static Accelerator? Parse(string? value) =>
        value is { Length: > 0 } && value.All(char.IsAsciiLetter) && Enum.TryParse<Accelerator>(value, ignoreCase: true, out var parsed)
            ? parsed
            : null;
}
