using System.Text;

namespace Kanal.Core.Text;

/// <summary>
/// Traditional→Simplified Chinese normalization. Gladia exposes a single "zh"
/// with no script variant and tends to emit Traditional characters, but the
/// primary Chinese participant is a mainland supplier — so the host normalizes
/// every piece of Chinese text before it enters room state or the relay.
/// Character-level conversion over OpenCC's TSCharacters table (Apache-2.0),
/// embedded as a resource: no runtime dependency, pure dictionary lookups.
/// One-to-many characters (乾, 髮, …) take OpenCC's first — most common —
/// mapping; phrase-level disambiguation is out of scope and documented as such.
/// </summary>
public static class SimplifiedChinese
{
    private static readonly Lazy<Dictionary<int, string>> Map =
        new(Load, LazyThreadSafetyMode.ExecutionAndPublication);

    public static bool IsChinese(string? langCode) =>
        langCode is not null &&
        (langCode.Equals("zh", StringComparison.OrdinalIgnoreCase) ||
         langCode.StartsWith("zh-", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Returns the text with Traditional characters replaced by their Simplified
    /// forms. Text that needs no change comes back as the same instance — partials
    /// arrive many times a second and the common case must allocate nothing.
    /// </summary>
    public static string Normalize(string text)
    {
        if (string.IsNullOrEmpty(text))
            return text;

        var map = Map.Value;
        StringBuilder? sb = null;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];

            // Everything below the CJK Radicals Supplement — all of Latin including
            // Polish diacritics — cannot be in the table; skip the lookup entirely.
            if (c < '⺀')
            {
                sb?.Append(c);
                continue;
            }

            int codePoint = c;
            var length = 1;
            if (char.IsHighSurrogate(c) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
            {
                codePoint = char.ConvertToUtf32(c, text[i + 1]);
                length = 2;
            }

            if (map.TryGetValue(codePoint, out var simplified))
            {
                sb ??= new StringBuilder(text.Length).Append(text, 0, i);
                sb.Append(simplified);
            }
            else
            {
                sb?.Append(text, i, length);
            }

            i += length - 1;
        }

        return sb?.ToString() ?? text;
    }

    private static Dictionary<int, string> Load()
    {
        using var stream = typeof(SimplifiedChinese).Assembly
                               .GetManifestResourceStream("Kanal.Core.Text.TSCharacters.txt")
                           ?? throw new InvalidOperationException(
                               "Embedded resource Kanal.Core.Text.TSCharacters.txt is missing.");
        using var reader = new StreamReader(stream, Encoding.UTF8);

        var map = new Dictionary<int, string>(6_000);
        while (reader.ReadLine() is { } line)
        {
            if (line.Length == 0 || line[0] == '#')
                continue;

            var tab = line.IndexOf('\t');
            if (tab <= 0)
                continue;

            var key = line[..tab];
            // keys are single characters — one code point, possibly a surrogate pair
            if (key.Length is not (1 or 2) || (key.Length == 2 && !char.IsHighSurrogate(key[0])))
                continue;

            var values = line[(tab + 1)..];
            var space = values.IndexOf(' ');
            var simplified = space >= 0 ? values[..space] : values; // one-to-many: first wins

            if (simplified != key) // identity rows would defeat the same-instance fast path
                map[char.ConvertToUtf32(key, 0)] = simplified;
        }

        return map;
    }
}
