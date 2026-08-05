using System.Xml.Linq;
using Kanal.Host.Services;

namespace Kanal.Core.UnitTests;

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

    [Fact]
    public void TheEmbeddedConversionTableIsCredited()
    {
        var table = Path.Combine(RepoRoot(), "src", "Kanal.Core", "Text", "TSCharacters.txt");
        Assert.True(File.Exists(table), "the table moved — this guard has to move with it");

        var notice = Assert.Single(OpenSourceNotices.All.Where(n => n.Name == "OpenCC"));
        Assert.Equal("Apache-2.0", notice.License);
    }

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

    private static IEnumerable<string> ShippedProjects()
    {
        var root = RepoRoot();
        foreach (var project in Directory.GetFiles(
                     Path.Combine(root, "src"), "*.csproj", SearchOption.AllDirectories))
            yield return project;
        foreach (var project in Directory.GetFiles(
                     Path.Combine(root, "tools"), "*.csproj", SearchOption.AllDirectories))
            yield return project;

        var props = Path.Combine(root, "Directory.Build.props");
        if (File.Exists(props))
            yield return props;
    }

    // XML, not a regex: a pattern anchored on Include being first missed reordered attributes.
    private static IEnumerable<string> PackageIds(string projectFile) =>
        XDocument.Load(projectFile)
            .Descendants()
            .Where(e => e.Name.LocalName == "PackageReference" && Distributed(e))
            .Select(e => (string?)e.Attribute("Include") ?? (string?)e.Attribute("Update"))
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id!.Trim());

    // A reference excluded from the shipped build is handed to nobody, so it carries no notice.
    private static bool Distributed(XElement reference) =>
        !reference.Elements().Any(child =>
            (child.Name.LocalName == "IncludeAssets" &&
             child.Value.Contains("None", StringComparison.OrdinalIgnoreCase)) ||
            (child.Name.LocalName == "PrivateAssets" &&
             child.Value.Contains("All", StringComparison.OrdinalIgnoreCase)));

    private static string RepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Kanal.slnx")))
            directory = directory.Parent;

        Assert.NotNull(directory);
        return directory.FullName;
    }
}
