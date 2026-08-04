using System.Text.RegularExpressions;
using Kanal.Host.Services;

namespace Kanal.Core.UnitTests;

/// <summary>
/// The list at the bottom of Settings. It is an obligation before it is a feature: the MIT and
/// BSD licences Kanal is assembled from all require their notice to travel with the binary, and a
/// list that silently falls behind the build is worse than no list, because it reads as complete.
/// </summary>
public class OpenSourceNoticeTests
{
    [Fact]
    public void EveryNoticeNamesAProjectALicenceAndAPlaceToReadIt()
    {
        Assert.NotEmpty(OpenSourceNotices.All);
        Assert.All(OpenSourceNotices.All, notice =>
        {
            Assert.False(string.IsNullOrWhiteSpace(notice.Name));
            Assert.False(string.IsNullOrWhiteSpace(notice.License));
            Assert.StartsWith("https://", notice.Url);
        });
    }

    [Fact]
    public void NoProjectIsListedTwice()
    {
        var names = OpenSourceNotices.All.Select(n => n.Name).ToList();
        Assert.Equal(names.Count, names.Distinct().Count());

        var packages = OpenSourceNotices.All.SelectMany(n => n.Packages).ToList();
        Assert.Equal(packages.Count, packages.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    /// <summary>
    /// The guard that keeps the list honest: a package added to any shipped project has to be
    /// named here. Test-only packages are exempt — they are not in what gets handed to anyone.
    /// </summary>
    [Fact]
    public void EveryShippedPackageIsAccountedFor()
    {
        var covered = OpenSourceNotices.All
            .SelectMany(n => n.Packages)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var referenced = ShippedProjects()
            .SelectMany(PackageIds)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        Assert.NotEmpty(referenced);
        var missing = referenced.Where(id => !covered.Contains(id)).Order().ToList();
        Assert.True(
            missing.Count == 0,
            $"Not named in Settings' open-source list: {string.Join(", ", missing)}");
    }

    /// <summary>And the reverse: a package dropped from the build must not linger in the list.</summary>
    [Fact]
    public void NothingIsCreditedThatIsNoLongerBuiltIn()
    {
        var referenced = ShippedProjects()
            .SelectMany(PackageIds)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var stale = OpenSourceNotices.All
            .SelectMany(n => n.Packages)
            .Where(id => !referenced.Contains(id))
            .Order()
            .ToList();

        Assert.True(stale.Count == 0, $"No longer referenced: {string.Join(", ", stale)}");
    }

    private static IEnumerable<string> ShippedProjects() =>
        Directory.GetFiles(Path.Combine(RepoRoot(), "src"), "*.csproj", SearchOption.AllDirectories);

    private static IEnumerable<string> PackageIds(string csproj) =>
        Regex.Matches(File.ReadAllText(csproj), "<PackageReference\\s+Include=\"([^\"]+)\"")
            .Select(m => m.Groups[1].Value);

    /// <summary>Walks up from the test binary until the solution file turns up.</summary>
    private static string RepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Kanal.slnx")))
            directory = directory.Parent;

        Assert.NotNull(directory);
        return directory.FullName;
    }
}
