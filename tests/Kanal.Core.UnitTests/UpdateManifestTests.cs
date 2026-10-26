using Kanal.Core.Updates;

namespace Kanal.Core.UnitTests;

public class UpdateManifestTests
{
    [Fact]
    public void ParsesAFullManifest()
    {
        const string json = """
            {
              "latest": "1.0.1",
              "minimumSupported": "1.0.0",
              "notes": "One or two lines for the operator.",
              "downloadUrl": "https://github.com/TONiiV/Kanal/releases/latest"
            }
            """;

        Assert.True(UpdateManifest.TryParse(json, out var manifest));
        Assert.Equal(new ReleaseVersion(1, 0, 1), manifest.Latest);
        Assert.Equal(new ReleaseVersion(1, 0, 0), manifest.MinimumSupported);
        Assert.Equal("One or two lines for the operator.", manifest.Notes);
        Assert.Equal(new Uri("https://github.com/TONiiV/Kanal/releases/latest"), manifest.DownloadUrl);
    }

    [Fact]
    public void ANullDownloadUrlMeansNoLink()
    {
        const string json = """
            {"latest":"1.0.1","minimumSupported":"1.0.0","notes":"n","downloadUrl":null}
            """;

        Assert.True(UpdateManifest.TryParse(json, out var manifest));
        Assert.Null(manifest.DownloadUrl);
    }

    [Fact]
    public void NotesAndDownloadUrlMayBeLeftOut()
    {
        const string json = """{"latest":"1.0.1","minimumSupported":"1.0.0"}""";

        Assert.True(UpdateManifest.TryParse(json, out var manifest));
        Assert.Null(manifest.Notes);
        Assert.Null(manifest.DownloadUrl);
    }

    [Fact]
    public void IgnoresFieldsItDoesNotKnow()
    {
        const string json = """
            {"latest":"1.0.1","minimumSupported":"1.0.0","channel":"beta","size":123}
            """;

        Assert.True(UpdateManifest.TryParse(json, out _));
    }

    [Theory]
    [InlineData("""{"minimumSupported":"1.0.0","notes":"n"}""")]
    [InlineData("""{"latest":"1.0.1","notes":"n"}""")]
    [InlineData("""{"latest":null,"minimumSupported":"1.0.0"}""")]
    [InlineData("""{"latest":"1.0.1","minimumSupported":null}""")]
    [InlineData("{}")]
    public void RefusesAManifestWithoutBothVersions(string json)
    {
        Assert.False(UpdateManifest.TryParse(json, out _));
    }

    [Theory]
    [InlineData("""{"latest":"v1.0.1","minimumSupported":"1.0.0"}""")]
    [InlineData("""{"latest":"1.0.1","minimumSupported":"1.0"}""")]
    [InlineData("""{"latest":"1.0.1-beta","minimumSupported":"1.0.0"}""")]
    [InlineData("""{"latest":"soon","minimumSupported":"1.0.0"}""")]
    [InlineData("""{"latest":1,"minimumSupported":"1.0.0"}""")]
    public void RefusesABadVersionString(string json)
    {
        Assert.False(UpdateManifest.TryParse(json, out _));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{\"latest\":")]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("\"1.0.1\"")]
    public void RefusesBodiesThatAreNotAManifest(string? json)
    {
        Assert.False(UpdateManifest.TryParse(json, out _));
    }

    [Theory]
    [InlineData("javascript:alert(1)")]
    [InlineData("http://example.test/kanal")]
    [InlineData("file:///etc/passwd")]
    [InlineData("not a url")]
    public void DropsADownloadUrlThatIsNotHttps(string url)
    {
        var json = $$"""{"latest":"1.0.1","minimumSupported":"1.0.0","downloadUrl":"{{url}}"}""";

        Assert.True(UpdateManifest.TryParse(json, out var manifest));
        Assert.Null(manifest.DownloadUrl);
    }
}
