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

    /// <summary>
    /// Every entry in the shipped file is hard-wrapped, because it is a Markdown file people read
    /// in a diff. A parser that keeps only the first physical line puts half-sentences on screen —
    /// "…kept for two weeks and never" — which is worse than showing nothing.
    /// </summary>
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

    /// <summary>A wrapped line is not a new entry, and it is not part of the next release either.</summary>
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

    /// <summary>
    /// A sub-bullet belongs to the entry above it; its marker is layout. Folded in raw it read
    /// "Parent entry. - first child - second child".
    /// </summary>
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

    /// <summary>
    /// The dialog is a TextBlock, not a Markdown renderer: backticks and asterisks belong to the
    /// file, and on screen they are punctuation in the middle of a sentence.
    /// </summary>
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
    /// The guard against the parser quietly cutting the file up: every line the dialog shows is a
    /// finished sentence. A truncated entry is easy to miss in a test that only counts entries and
    /// impossible to miss on screen.
    /// </summary>
    [Fact]
    public void EveryShippedChangeIsAWholeSentence()
    {
        foreach (var release in Changelog.Releases)
        foreach (var change in release.Changes)
            Assert.True(change.EndsWith('.'), $"{release.Version}: \"{change}\" stops mid-sentence.");
    }

    /// <summary>
    /// The changelog is a screen in the host like any other, so the unbranded rule reaches it:
    /// the dialog must not be where a vendor's name turns up after being kept off every label.
    /// </summary>
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
