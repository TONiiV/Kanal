using System.Runtime.InteropServices;
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

    // LLamaSharp itself tries CUDA, then Vulkan, then CPU, and falls back when a native library fails
    // to load. Kanal only switches the GPU variants off on request and reports which one loaded.
    public static Accelerator EnsureConfigured()
    {
        lock (Gate)
        {
            if (_active is { } active)
                return active;

            var forced = Parse(Environment.GetEnvironmentVariable(OverrideVariable));
            var config = NativeLibraryConfig.All;
            if (!NativeLibraryConfig.LLama.LibraryHasLoaded)
                config.WithCuda(forced is null or Accelerator.Cuda)
                    .WithVulkan(forced is null or Accelerator.Vulkan)
                    .WithAutoFallback(true);

            config.DryRun(out var llama, out _);
            var appleSilicon = OperatingSystem.IsMacOS() && RuntimeInformation.OSArchitecture == Architecture.Arm64;
            _active = Classify(llama?.Metadata?.UseCuda == true, llama?.Metadata?.UseVulkan == true, appleSilicon, forced);
            return _active.Value;
        }
    }

    // The osx-arm64 build in LLamaSharp.Backend.Cpu carries Metal; it reports neither CUDA nor Vulkan.
    internal static Accelerator Classify(bool useCuda, bool useVulkan, bool appleSilicon, Accelerator? forced) =>
        forced == Accelerator.Cpu ? Accelerator.Cpu
        : useCuda ? Accelerator.Cuda
        : useVulkan ? Accelerator.Vulkan
        : appleSilicon ? Accelerator.Metal
        : Accelerator.Cpu;

    internal static Accelerator? Parse(string? value) =>
        Enum.TryParse<Accelerator>(value, ignoreCase: true, out var parsed) && Enum.IsDefined(parsed) ? parsed : null;
}
