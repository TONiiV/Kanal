using System.Text.RegularExpressions;

namespace Kanal.Core.UnitTests;

public class WindowsLicenseRtfTests
{
    static string Words(string text) => Regex.Replace(text, @"\s+", " ").Trim();

    [Fact]
    public void InstallerLicenseTextMatchesLicenseFile()
    {
        var root = InstallerLayoutTests.FindRepoRoot();
        var rtf = File.ReadAllText(Path.Combine(root, "installers", "windows", "License.rtf"));
        var license = File.ReadAllText(Path.Combine(root, "LICENSE"));

        var plain = Regex.Replace(rtf, @"\{\\fonttbl.*?\}\}", "");
        plain = Regex.Replace(plain, @"\\[a-z]+-?\d* ?|[{}]", "");

        Assert.Equal(Words(license), Words(plain));
    }
}
