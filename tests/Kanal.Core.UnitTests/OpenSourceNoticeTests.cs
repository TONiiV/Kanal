using System.Text.RegularExpressions;
using System.Xml.Linq;
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

    /// <summary>
    /// The obligation a package scan cannot see: OpenCC's conversion table is compiled into
    /// <c>Kanal.Core</c> as an embedded resource, and Apache-2.0 is the one licence here that
    /// spells out that its notice travels with the work.
    /// </summary>
    [Fact]
    public void TheEmbeddedConversionTableIsCredited()
    {
        var table = Path.Combine(RepoRoot(), "src", "Kanal.Core", "Text", "TSCharacters.txt");
        Assert.True(File.Exists(table), "the table moved — this guard has to move with it");

        var notice = Assert.Single(OpenSourceNotices.All.Where(n => n.Name == "OpenCC"));
        Assert.Equal("Apache-2.0", notice.License);
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

    /// <summary>
    /// Everything whose packages end up in what an operator is handed: the host and the libraries
    /// it references, the diagnostic tool that ships beside them, and the props file that injects
    /// references into all of them at once. Not the test projects — those are not distributed.
    /// </summary>
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

    /// <summary>
    /// Read as XML, not scanned with a regex. A pattern anchored on <c>Include</c> being the first
    /// attribute misses <c>&lt;PackageReference Version="…" Include="…" /&gt;</c> and
    /// single-quoted attributes — both valid MSBuild, both what a format-on-save or a dependency
    /// bot produces — so an uncredited package sailed through, and merely reordering the
    /// attributes on a package still being shipped reported it as stale.
    /// </summary>
    private static IEnumerable<string> PackageIds(string projectFile) =>
        XDocument.Load(projectFile)
            .Descendants()
            .Where(e => e.Name.LocalName == "PackageReference" && Distributed(e))
            .Select(e => (string?)e.Attribute("Include") ?? (string?)e.Attribute("Update"))
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id!.Trim());

    /// <summary>
    /// A reference kept out of the shipped build — a debugging aid excluded from Release — is not
    /// handed to anyone, so it carries no notice obligation for the binary. Crediting it anyway
    /// would mean publishing a licence claim about a package nobody receives, which is how a
    /// licence this list could not substantiate ended up on a screen headed "open source".
    /// </summary>
    private static bool Distributed(XElement reference) =>
        !reference.Elements().Any(child =>
            AppliesToTheShippedBuild(child) &&
            ((child.Name.LocalName == "IncludeAssets" &&
              child.Value.Contains("None", StringComparison.OrdinalIgnoreCase)) ||
             (child.Name.LocalName == "PrivateAssets" &&
              child.Value.Contains("All", StringComparison.OrdinalIgnoreCase))));

    /// <summary>The configuration an operator is handed, and the only one this list answers for.</summary>
    private const string ShippedConfiguration = "Release";

    /// <summary>
    /// Whether an exclusion applies to that build. <c>Condition</c> is the whole difference between
    /// "kept out of Release" and "kept out of Debug", so it is evaluated rather than ignored — only
    /// the one shape the repository writes, and anything else counts as shipping: a package
    /// credited needlessly costs a line, one missed costs the obligation.
    /// </summary>
    private static bool AppliesToTheShippedBuild(XElement element)
    {
        var condition = (string?)element.Attribute("Condition");
        if (string.IsNullOrWhiteSpace(condition))
            return true;

        var comparison = Regex.Match(
            condition, @"^\s*'\$\(Configuration\)'\s*(==|!=)\s*'([^']*)'\s*$");
        if (!comparison.Success)
            return false;

        var names = string.Equals(
            comparison.Groups[2].Value, ShippedConfiguration, StringComparison.OrdinalIgnoreCase);
        return comparison.Groups[1].Value == "==" ? names : !names;
    }

    /// <summary>
    /// The exemption reads an element's text; the <c>Condition</c> beside it decides whether that
    /// element applies to the build anyone is handed. The two forms differ by one operator, and
    /// ignoring the attribute reports the shipping one exempt — an uncredited package under a list
    /// that reads as complete, which is the failure this whole guard exists to prevent.
    /// </summary>
    [Fact]
    public void AnExclusionThatDoesNotApplyToTheShippedBuildIsNoExemption()
    {
        // What Kanal.Host writes: kept out of everything that is not Debug, so out of Release.
        Assert.False(Distributed(XElement.Parse(
            """
            <PackageReference Include="DebuggingAid" Version="1.0">
              <PrivateAssets Condition="'$(Configuration)' != 'Debug'">All</PrivateAssets>
            </PackageReference>
            """)));

        // The inverse: kept out of Debug, shipped in Release.
        Assert.True(Distributed(XElement.Parse(
            """
            <PackageReference Include="Shipped" Version="1.0">
              <PrivateAssets Condition="'$(Configuration)' == 'Debug'">All</PrivateAssets>
            </PackageReference>
            """)));

        // A condition this guard cannot read counts as shipping: a package credited needlessly
        // costs a line, one missed costs the obligation.
        Assert.True(Distributed(XElement.Parse(
            """
            <PackageReference Include="Unreadable" Version="1.0">
              <IncludeAssets Condition="'$(Foo)' == 'bar' And '$(Baz)' != ''">None</IncludeAssets>
            </PackageReference>
            """)));

        // And the plain unconditional exclusion is still an exemption.
        Assert.False(Distributed(XElement.Parse(
            """
            <PackageReference Include="Analyzer" Version="1.0">
              <PrivateAssets>all</PrivateAssets>
            </PackageReference>
            """)));
    }

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
