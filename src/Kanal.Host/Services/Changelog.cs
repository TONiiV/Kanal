using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace Kanal.Host.Services;

public sealed record ChangelogRelease(string Version, DateOnly? Date, IReadOnlyList<string> Changes);

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

            if (version is null)
                continue;

            if (Bullet().Match(line) is { Success: true } bullet)
            {
                changes.Add(Plain(bullet.Groups["text"].Value.Trim()));
                open = true;
                continue;
            }

            // An indented line under an open bullet is that bullet, wrapped: every entry in the
            // file is hard-wrapped, and taking only the first line put half-sentences on screen.
            if (open && changes.Count > 0 && Continuation().IsMatch(line))
            {
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

    // The dialog is a TextBlock, not a Markdown renderer.
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

    [GeneratedRegex(@"^##\s+v?(?<version>[0-9][^\s]*)(?:\s+[—–-]\s+(?<date>\d{4}-\d{2}-\d{2}))?\s*$")]
    private static partial Regex VersionHeading();

    // Hard against the margin: an indented marker is a sub-list, folded into the entry above.
    [GeneratedRegex(@"^[-*]\s+(?<text>.+)$")]
    private static partial Regex Bullet();

    [GeneratedRegex(@"^\s+\S")]
    private static partial Regex Continuation();

    [GeneratedRegex(@"`([^`]*)`|\*\*([^*]+)\*\*")]
    private static partial Regex InlineMarkup();
}
