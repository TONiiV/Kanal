using System.Text.RegularExpressions;

namespace Kanal.Providers.LocalMt;

/// <summary>
/// Deterministic cleanup of raw LLM output: thinking blocks, wrapping quotes and
/// "Translation:" labels are stripped; the translation text itself — part numbers,
/// standards, units — is never rewritten.
/// </summary>
public static partial class MtOutputCleaner
{
    [GeneratedRegex(@"<think>.*?</think>", RegexOptions.Singleline)]
    private static partial Regex ThinkBlock();

    [GeneratedRegex(@"^\s*(Translation|Übersetzung|Tłumaczenie|翻译|译文)\s*[:：]\s*", RegexOptions.IgnoreCase)]
    private static partial Regex TranslationLabel();

    private static readonly (char Open, char Close)[] QuotePairs =
    [
        ('"', '"'),
        ('“', '”'), // “ ”
        ('„', '“'), // „ “
        ('«', '»'),
        ('「', '」'),
        ('『', '』'),
    ];

    public static string Clean(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return "";

        var text = ThinkBlock().Replace(raw, "");

        // an opened-but-never-closed think block means the model spent its whole
        // budget reasoning — there is no translation in there to salvage
        var unterminated = text.IndexOf("<think>", StringComparison.Ordinal);
        if (unterminated >= 0)
            text = text[..unterminated];

        text = text.Trim();
        text = TranslationLabel().Replace(text, "").Trim();

        foreach (var (open, close) in QuotePairs)
        {
            // A quote at each end is not the same as a quoted line: «ISO 7599» dotyczy
            // «KX-4402» opens and closes twice. Only strip when nothing between the ends
            // closes the span first — otherwise the standard and the part number, the two
            // things this class exists to leave alone, come out with a quote welded on.
            if (text.Length >= 2 &&
                text[0] == open &&
                text[^1] == close &&
                text.IndexOf(close, 1) == text.Length - 1)
            {
                text = text[1..^1].Trim();
                break;
            }
        }

        return text;
    }
}
