using Kanal.Core.Updates;

namespace Kanal.Core.UnitTests;

public class UpdatePolicyTests
{
    [Theory]
    [InlineData("1.0.0", "1.0.0", "1.0.0", UpdateStatus.UpToDate)]
    [InlineData("1.0.1", "1.0.1", "1.0.0", UpdateStatus.UpToDate)]
    [InlineData("1.0.0", "1.0.1", "1.0.0", UpdateStatus.UpdateAvailable)]
    [InlineData("1.0.0", "1.1.0", "1.0.0", UpdateStatus.UpdateAvailable)]
    [InlineData("1.1.0", "1.1.0", "1.1.0", UpdateStatus.UpToDate)]
    [InlineData("1.0.9", "1.2.0", "1.1.0", UpdateStatus.Unsupported)]
    [InlineData("1.0.0", "1.0.0", "1.0.1", UpdateStatus.Unsupported)]
    [InlineData("2.0.0", "1.0.0", "1.0.0", UpdateStatus.UpToDate)]
    public void JudgesTheCurrentVersionAgainstTheManifest(
        string current, string latest, string minimum, UpdateStatus expected)
    {
        var manifest = new UpdateManifest(Parse(latest), Parse(minimum), null, null);

        Assert.Equal(expected, UpdatePolicy.Evaluate(Parse(current), manifest));
    }

    [Fact]
    public void ATooOldVersionIsUnsupportedEvenThoughAnUpdateExists()
    {
        var manifest = new UpdateManifest(Parse("2.0.0"), Parse("1.5.0"), null, null);

        Assert.Equal(UpdateStatus.Unsupported, UpdatePolicy.Evaluate(Parse("1.0.0"), manifest));
    }

    private static ReleaseVersion Parse(string text)
    {
        Assert.True(ReleaseVersion.TryParse(text, out var version));
        return version;
    }
}
