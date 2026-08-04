using Kanal.Host.Services;

namespace Kanal.Core.UnitTests;

/// <summary>
/// What changed between the build the operator had last week and the one they have now — readable
/// from inside the application, because the machine running a meeting is not the machine someone
/// browses a repository on.
/// </summary>
public class ChangelogTests
{
    private const string Sample = """
        # Changelog

        Some preamble nobody needs to see in the dialog.

        ## 0.4.0 — 2026-08-04

        - Log files, with a level and a size limit.
        - The list of open-source projects Kanal is built on.

        ## 0.3.0 — 2026-07-19

        - The host speaks four languages.
        """;

    [Fact]
    public void ReleasesComeOutNewestFirstWithTheirDates()
    {
        var releases = Changelog.Parse(Sample);

        Assert.Equal(["0.4.0", "0.3.0"], releases.Select(r => r.Version));
        Assert.Equal(new DateOnly(2026, 8, 4), releases[0].Date);
        Assert.Equal(new DateOnly(2026, 7, 19), releases[1].Date);
    }

    [Fact]
    public void EachChangeBelongsToTheReleaseItWasWrittenUnder()
    {
        var releases = Changelog.Parse(Sample);

        Assert.Equal(
            ["Log files, with a level and a size limit.", "The list of open-source projects Kanal is built on."],
            releases[0].Changes);
        Assert.Equal(["The host speaks four languages."], releases[1].Changes);
    }

    /// <summary>Everything above the first version heading is for the file's readers, not the dialog's.</summary>
    [Fact]
    public void ThePreambleIsNotAChange()
    {
        var releases = Changelog.Parse(Sample);

        Assert.DoesNotContain(releases, r => r.Changes.Any(c => c.Contains("preamble")));
    }

    [Fact]
    public void AVersionWithoutADateStillParses()
    {
        var releases = Changelog.Parse("## 0.5.0\n\n- Something not released yet.\n");

        var release = Assert.Single(releases);
        Assert.Equal("0.5.0", release.Version);
        Assert.Null(release.Date);
        Assert.Equal(["Something not released yet."], release.Changes);
    }

    [Fact]
    public void AnEmptyFileIsNoReleasesRatherThanACrash()
    {
        Assert.Empty(Changelog.Parse(""));
        Assert.Empty(Changelog.Parse("# Changelog\n\nNothing yet.\n"));
    }

    // ---- the file that actually ships ------------------------------------------------------

    [Fact]
    public void TheShippedChangelogParsesIntoDatedReleases()
    {
        Assert.NotEmpty(Changelog.Releases);
        Assert.All(Changelog.Releases, release =>
        {
            Assert.NotNull(release.Date);
            Assert.NotEmpty(release.Changes);
        });

        var versions = Changelog.Releases.Select(r => r.Version).ToList();
        Assert.Equal(versions.Count, versions.Distinct().Count());
    }

    /// <summary>
    /// The one guard that keeps this honest: the build the operator is running has to be the entry
    /// they are reading at the top. Releasing is "write the entry, bump the version" — in either
    /// order, but never only one.
    /// </summary>
    [Fact]
    public void TheNewestEntryIsTheVersionThisBuildReports()
    {
        Assert.Equal(AppVersion.Current, Changelog.Releases[0].Version);
    }

    [Fact]
    public void ReleasesAreListedNewestFirst()
    {
        var dates = Changelog.Releases.Select(r => r.Date!.Value).ToList();

        Assert.Equal(dates.OrderByDescending(d => d), dates);
    }
}
