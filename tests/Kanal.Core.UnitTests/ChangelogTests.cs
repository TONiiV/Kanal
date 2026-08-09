using Kanal.Host.Services;

namespace Kanal.Core.UnitTests;

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

    [Fact]
    public void ThePreambleIsNotAChange()
    {
        var releases = Changelog.Parse(Sample);

        Assert.DoesNotContain(releases, r => r.Changes.Any(c => c.Contains("preamble")));
    }

    [Fact]
    public void AWrappedBulletKeepsTheWholeSentence()
    {
        var releases = Changelog.Parse("""
            ## 0.6.0 — 2026-08-04

            - One sentence that runs past the column limit and therefore
              continues on the next line, and then
              on a third.
            - A short one.
            """);

        Assert.Equal(
            [
                "One sentence that runs past the column limit and therefore continues on the next line, and then on a third.",
                "A short one.",
            ],
            releases[0].Changes);
    }

    [Fact]
    public void AContinuationLineDoesNotLeakIntoTheNextRelease()
    {
        var releases = Changelog.Parse("""
            ## 0.6.0 — 2026-08-04

            - Something that wraps
              onto a second line.

            ## 0.5.0 — 2026-08-01

            - Something else.
            """);

        Assert.Equal(["Something that wraps onto a second line."], releases[0].Changes);
        Assert.Equal(["Something else."], releases[1].Changes);
    }

    [Fact]
    public void ASubBulletJoinsItsParentWithoutItsMarker()
    {
        var releases = Changelog.Parse("""
            ## 0.6.0 — 2026-08-04

            - Parent entry:
              - first child,
              - second child.
            """);

        Assert.Equal(["Parent entry: first child, second child."], releases[0].Changes);
    }

    [Fact]
    public void InlineMarkupIsStrippedForTheScreen()
    {
        var releases = Changelog.Parse("""
            ## 0.6.0 — 2026-08-04

            - Logs land in `%APPDATA%/Kanal/logs`, under **Settings → Diagnostics**.
            """);

        Assert.Equal(
            ["Logs land in %APPDATA%/Kanal/logs, under Settings → Diagnostics."],
            releases[0].Changes);
    }

    [Fact]
    public void NoShippedEntryCarriesRawMarkup()
    {
        foreach (var release in Changelog.Releases)
        foreach (var change in release.Changes)
        {
            Assert.DoesNotContain('`', change);
            Assert.DoesNotContain("**", change);
        }
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

    [Fact]
    public void TheShippedChangelogParsesIntoReleases()
    {
        Assert.NotEmpty(Changelog.Releases);
        Assert.All(Changelog.Releases, release => Assert.NotEmpty(release.Changes));

        var versions = Changelog.Releases.Select(r => r.Version).ToList();
        Assert.Equal(versions.Count, versions.Distinct().Count());
    }

    [Fact]
    public void OnlyTheVersionBeingWorkedTowardsIsUndated()
    {
        Assert.All(Changelog.Releases.Skip(1), release =>
            Assert.True(release.Date is not null, $"{release.Version} was released without a date."));
    }

    [Fact]
    public void EveryShippedChangeIsAWholeSentence()
    {
        foreach (var release in Changelog.Releases)
        foreach (var change in release.Changes)
            Assert.True(change.EndsWith('.'), $"{release.Version}: \"{change}\" stops mid-sentence.");
    }

    [Fact]
    public void NoReleaseNamesAVendor()
    {
        string[] vendors =
            ["Gladia", "Whisper", "DeepL", "Google", "OpenAI", "Claude", "Anthropic", "Qwen", "Gemma", "Supabase"];

        foreach (var release in Changelog.Releases)
        foreach (var change in release.Changes)
        foreach (var vendor in vendors)
            Assert.False(
                change.Contains(vendor, StringComparison.OrdinalIgnoreCase),
                $"{release.Version} names {vendor}.");
    }

    [Fact]
    public void TheNewestEntryIsTheVersionThisBuildReports()
    {
        Assert.Equal(AppVersion.Current, Changelog.Releases[0].Version);
    }

    [Fact]
    public void ReleasesAreListedNewestFirst()
    {
        var dates = Changelog.Releases.Where(r => r.Date is not null).Select(r => r.Date!.Value).ToList();

        Assert.Equal(dates.OrderByDescending(d => d), dates);
    }
}
