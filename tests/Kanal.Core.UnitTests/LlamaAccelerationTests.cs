using Kanal.Providers.LocalMt;

namespace Kanal.Core.UnitTests;

public class LlamaAccelerationTests
{
    [Theory]
    [InlineData(true, false, false, Accelerator.Cuda)]
    [InlineData(false, true, false, Accelerator.Vulkan)]
    [InlineData(false, false, true, Accelerator.Metal)]
    [InlineData(false, false, false, Accelerator.Cpu)]
    public void Classify_names_the_loaded_native_library(bool cuda, bool vulkan, bool appleSilicon, Accelerator expected) =>
        Assert.Equal(expected, LlamaAcceleration.Classify(cuda, vulkan, appleSilicon, forced: null));

    [Fact]
    public void A_forced_CPU_wins_over_Metal() =>
        Assert.Equal(Accelerator.Cpu, LlamaAcceleration.Classify(false, false, appleSilicon: true, Accelerator.Cpu));

    [Theory]
    [InlineData("cpu", Accelerator.Cpu)]
    [InlineData("Vulkan", Accelerator.Vulkan)]
    [InlineData("CUDA", Accelerator.Cuda)]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData("gpu", null)]
    [InlineData("7", null)]
    public void Parse_reads_the_override_variable(string? value, Accelerator? expected) =>
        Assert.Equal(expected, LlamaAcceleration.Parse(value));
}
