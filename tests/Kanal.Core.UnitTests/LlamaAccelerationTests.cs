using Kanal.Providers.LocalMt;

namespace Kanal.Core.UnitTests;

public class LlamaAccelerationTests
{
    [Theory]
    [InlineData(true, false, true, false, Accelerator.Cuda)]
    [InlineData(false, true, true, false, Accelerator.Vulkan)]
    [InlineData(false, false, true, true, Accelerator.Metal)]
    [InlineData(false, false, false, false, Accelerator.Cpu)]
    public void Classify_names_the_loaded_native_library(bool cuda, bool vulkan, bool offload, bool macOS, Accelerator expected) =>
        Assert.Equal(expected, LlamaAcceleration.Classify(cuda, vulkan, offload, macOS, forced: null));

    [Theory]
    [InlineData(false, true, false, true)]
    [InlineData(false, false, false, true)]
    public void A_library_without_GPU_offload_is_CPU(bool cuda, bool vulkan, bool offload, bool macOS) =>
        Assert.Equal(Accelerator.Cpu, LlamaAcceleration.Classify(cuda, vulkan, offload, macOS, forced: null));

    [Fact]
    public void A_forced_CPU_wins_over_Metal() =>
        Assert.Equal(Accelerator.Cpu, LlamaAcceleration.Classify(false, false, gpuOffload: true, macOS: true, Accelerator.Cpu));

    [Theory]
    [InlineData("cpu", Accelerator.Cpu)]
    [InlineData("Vulkan", Accelerator.Vulkan)]
    [InlineData("CUDA", Accelerator.Cuda)]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData("gpu", null)]
    [InlineData("7", null)]
    [InlineData("1", null)]
    public void Parse_reads_the_override_variable(string? value, Accelerator? expected) =>
        Assert.Equal(expected, LlamaAcceleration.Parse(value));
}
