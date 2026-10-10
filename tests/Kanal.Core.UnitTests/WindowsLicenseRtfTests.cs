using System.Text.RegularExpressions;

namespace Kanal.Core.UnitTests;

public class WindowsLicenseRtfTests
{
    static string FindRepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "Kanal.slnx")))
            dir = Path.GetDirectoryName(dir);

        return dir ?? throw new InvalidOperationException("Kanal.slnx not found above the test assembly");
    }

    static string Words(string text) => Regex.Replace(text, @"\s+", " ").Trim();

    [Fact]
    public void InstallerLicenseTextMatchesLicenseFile()
    {
        var root = FindRepoRoot();
        var rtf = File.ReadAllText(Path.Combine(root, "installers", "windows", "License.rtf"));
        var license = File.ReadAllText(Path.Combine(root, "LICENSE"));

        var plain = Regex.Replace(rtf, @"\{\\fonttbl.*?\}\}", "");
        plain = Regex.Replace(plain, @"\\[a-z]+-?\d* ?|[{}]", "");

        Assert.Equal(Words(license), Words(plain));
    }
}
