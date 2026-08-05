using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace Kanal.Host.Services;

/// <summary>One version and what changed in it.</summary>
public sealed record ChangelogRelease(string Version, DateOnly? Date, IReadOnlyList<string> Changes);

/// <summary>
/// <c>CHANGELOG.md</c>, embedded in the executable and parsed for the dialog behind Settings.
/// Read from inside the application because the laptop running a meeting is not the machine
/// anybody browses a repository on — and the question "did this change since last week?" is asked
/// in the room, by the person who noticed.
/// </summary>
/// <remarks>
/// The file stays plain Markdown, readable on GitHub and in a diff, rather than becoming a data
/// format only this parser understands: <c>## &lt;version&gt; — &lt;yyyy-MM-dd&gt;</c> headings,
/// with <c>-</c> or <c>*</c> bullets under them. Anything else in the file is prose for readers of
/// the file and is skipped here.
/// </remarks>
public static partial class Changelog
{
    private const string ResourceName = "Kanal.Host.CHANGELOG.md";

    public static IReadOnlyList<ChangelogRelease> Releases { get; } = Parse(ReadEmbedded());

    public static IReadOnlyList<ChangelogRelease> Parse(string markdown)
    {
        var releases = new List<ChangelogRelease>();
        string? version = null;
        DateOnly? date = null;
        var changes = new List<string>();
        var open = false; // is the last bullet still taking wrapped lines?

        void Flush()
        {
            if (version is not null)
                releases.Add(new ChangelogRelease(version, date, changes.ToArray()));
        }

        foreach (var raw in markdown.Split('\n'))
        {
            var line = raw.TrimEnd('\r', ' ', '\t');
            if (VersionHeading().Match(line) is { Success: true } heading)
            {
                Flush();
                version = heading.Groups["version"].Value;
                date = DateOnly.TryParseExact(
                    heading.Groups["date"].Value, "yyyy-MM-dd",
                    CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)
                    ? parsed
                    : null;
                changes = [];
                continue;
            }

            // Bullets above the first version heading belong to the file's own preamble.
            if (version is null)
                continue;

            if (Bullet().Match(line) is { Success: true } bullet)
            {
                changes.Add(Plain(bullet.Groups["text"].Value.Trim()));
                open = true;
                continue;
            }

            // An indented line under an open bullet is that bullet, wrapped. The file is written
            // to be read in a diff, so every entry is hard-wrapped — taking only the first
            // physical line put half-sentences on screen, which is worse than showing nothing.
            // A blank line closes the bullet, so a paragraph that follows one is not swallowed
            // into it.
            if (open && changes.Count > 0 && Continuation().IsMatch(line))
            {
                // An indented sub-bullet is still this entry's text; its marker is layout, and
                // folding it in raw produced "Parent entry. - first child - second child".
                var text = line.Trim();
                if (Bullet().Match(text) is { Success: true } nested)
                    text = nested.Groups["text"].Value.Trim();
                changes[^1] = $"{changes[^1]} {Plain(text)}";
            }
            else
            {
                open = false;
            }
        }

        Flush();
        return releases;
    }

    /// <summary>
    /// Strips the inline Markdown the dialog cannot render. `%APPDATA%` and *View changelog* are
    /// code and emphasis in a file people read in a diff; in a TextBlock they are backticks and
    /// asterisks sitting in the middle of a sentence.
    /// </summary>
    private static string Plain(string markdown) =>
        InlineMarkup().Replace(markdown, m => m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value);

    private static string ReadEmbedded()
    {
        using var stream = typeof(Changelog).Assembly.GetManifestResourceStream(ResourceName);
        if (stream is null)
            return "";

        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    /// <summary>An em dash or a hyphen between version and date — both read the same on a heading.</summary>
    [GeneratedRegex(@"^##\s+v?(?<version>[0-9][^\s]*)(?:\s+[—–-]\s+(?<date>\d{4}-\d{2}-\d{2}))?\s*$")]
    private static partial Regex VersionHeading();

    /// <summary>
    /// A new entry starts hard against the margin. An indented marker is a sub-list in Markdown —
    /// it belongs to the entry above, and the continuation branch folds it in.
    /// </summary>
    [GeneratedRegex(@"^[-*]\s+(?<text>.+)$")]
    private static partial Regex Bullet();

    /// <summary>An indented line that is not itself a bullet: the previous bullet, wrapped.</summary>
    [GeneratedRegex(@"^\s+\S")]
    private static partial Regex Continuation();

    /// <summary>Code spans and emphasis: keep what is inside, drop the marks.</summary>
    [GeneratedRegex(@"`([^`]*)`|\*\*([^*]+)\*\*")]
    private static partial Regex InlineMarkup();
}
