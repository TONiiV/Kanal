using Kanal.Core.Providers.Testing;
using Kanal.Providers.Gladia;

namespace Kanal.Core.UnitTests;

public class DiarizationCapabilityTests
{
    /// <summary>
    /// Gladia's live endpoint has no diarization parameter — it exists only on the pre-recorded
    /// one — so every live transcript arrives without a speaker and <c>GladiaWire</c> falls back
    /// to a single tag. The capability table is the only thing the orchestrator reasons from.
    /// </summary>
    [Fact]
    public void GladiaDoesNotClaimToSeparateSpeakers()
    {
        using var gladia = new GladiaAsrProvider(new GladiaOptions { ApiKey = "test" });

        Assert.False(gladia.Caps.Diarization);
    }

    [Fact]
    public void TheLiveRequestBodyAsksForNoDiarization()
    {
        var body = GladiaAsrProvider.BuildInitBody(
            new GladiaOptions { ApiKey = "test" },
            new Core.Providers.AsrSessionOptions(16_000, ["zh", "de", "pl"]));

        Assert.False(body.ContainsKey("diarization"));
        Assert.False(body.ContainsKey("diarization_config"));
    }

    /// <summary>The one provider that keeps the "the provider brings its own" path under test.</summary>
    [Fact]
    public void TheFakeProviderStillClaimsIt()
    {
        Assert.True(new FakeAsrProvider().Caps.Diarization);
    }
}
