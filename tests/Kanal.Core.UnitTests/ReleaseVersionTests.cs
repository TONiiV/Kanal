using Kanal.Core.Updates;

namespace Kanal.Core.UnitTests;

public class ReleaseVersionTests
{
    [Theory]
    [InlineData("1.0.0", 1, 0, 0)]
    [InlineData("0.0.0", 0, 0, 0)]
    [InlineData("12.34.567", 12, 34, 567)]
    public void ParsesThreeNumbers(string text, int major, int minor, int patch)
    {
        Assert.True(ReleaseVersion.TryParse(text, out var version));
        Assert.Equal(new ReleaseVersion(major, minor, patch), version);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("1")]
    [InlineData("1.0")]
    [InlineData("1.0.0.0")]
    [InlineData("1.0.0-beta")]
    [InlineData("1.0.0+abc")]
    [InlineData("v1.0.0")]
    [InlineData("1.a.0")]
    [InlineData("-1.0.0")]
    [InlineData("1..0")]
    [InlineData("99999999999.0.0")]
    public void RejectsAnythingThatIsNotThreeNumbers(string? text)
    {
        Assert.False(ReleaseVersion.TryParse(text, out _));
    }

    [Theory]
    [InlineData("1.0.1", "1.0.0")]
    [InlineData("1.1.0", "1.0.9")]
    [InlineData("2.0.0", "1.99.99")]
    [InlineData("1.10.0", "1.9.9")]
    [InlineData("1.0.10", "1.0.9")]
    public void ComparesNumbersAndNotText(string higher, string lower)
    {
        var high = Parse(higher);
        var low = Parse(lower);

        Assert.True(high > low);
        Assert.True(high >= low);
        Assert.True(low < high);
        Assert.True(low <= high);
        Assert.True(high.CompareTo(low) > 0);
        Assert.True(low.CompareTo(high) < 0);
    }

    [Fact]
    public void EqualVersionsCompareEqual()
    {
        var one = Parse("1.2.3");
        var other = Parse("1.2.3");

        Assert.Equal(0, one.CompareTo(other));
        Assert.True(one <= other);
        Assert.True(one >= other);
        Assert.False(one < other);
        Assert.False(one > other);
    }

    [Fact]
    public void PrintsTheSameTextItWasParsedFrom()
    {
        Assert.Equal("1.2.3", Parse("1.2.3").ToString());
    }

    private static ReleaseVersion Parse(string text)
    {
        Assert.True(ReleaseVersion.TryParse(text, out var version), $"{text} did not parse.");
        return version;
    }
}
