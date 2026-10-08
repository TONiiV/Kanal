using System.Text.RegularExpressions;

namespace Kanal.Providers.LocalAsr;

// The model takes the language as input and does not report what it heard, so the source language
// is read back from the text and the room's own languages.
// ponytail: script + stopword vote; a short Latin sentence with no stopword falls to the first
// Latin room language. Upgrade path: a dedicated language-identification model.
public static class TranscriptLanguage
{
    private static readonly Dictionary<string, (string Letters, HashSet<string> Words)> Latin = new()
    {
        ["de"] = ("äöüß", ["der", "die", "das", "und", "ist", "nicht", "ich", "wir", "sie", "es", "ein", "eine",
            "für", "mit", "den", "dem", "zu", "auf", "im", "noch", "sind", "wird", "auch", "bitte"]),
        ["pl"] = ("ąćęłńóśźż", ["i", "w", "nie", "się", "to", "jest", "na", "że", "z", "do", "czy", "jak", "ale",
            "tak", "dla", "tej", "są", "przed", "po", "musimy", "proszę"]),
        ["en"] = ("", ["the", "and", "is", "to", "of", "a", "in", "that", "it", "we", "for", "this", "are", "not"]),
        ["fr"] = ("çàâèéêëîïôûœ", ["le", "la", "les", "et", "est", "des", "une", "pas", "que", "pour", "nous"]),
        ["es"] = ("ñáíóú¿¡", ["el", "la", "los", "y", "es", "que", "de", "en", "no", "para", "una"]),
        ["it"] = ("àèéìòù", ["il", "la", "che", "e", "di", "non", "per", "una", "sono", "con"]),
        ["cs"] = ("ěščřžýůťď", ["a", "je", "to", "se", "na", "že", "v", "ne", "jsou", "pro"]),
    };

    public static string Guess(string text, IReadOnlyList<string> room)
    {
        var script = ByScript(text, room);
        if (script is not null && room.Contains(script))
            return script;

        var lower = text.ToLowerInvariant();
        var words = Regex.Split(lower, @"\P{L}+");

        var best = room
            .Where(Latin.ContainsKey)
            .Select(code => (code, score:
                3 * lower.Count(Latin[code].Letters.Contains) + 2 * words.Count(Latin[code].Words.Contains)))
            .OrderByDescending(c => c.score)
            .FirstOrDefault();

        return best.code ?? room.FirstOrDefault(Latin.ContainsKey) ?? room[0];
    }

    private static string? ByScript(string text, IReadOnlyList<string> room)
    {
        if (text.Any(c => c is >= '가' and <= '힯'))
            return "ko";
        if (text.Any(c => c is >= '぀' and <= 'ヿ'))
            return "ja";
        if (text.Any(c => c is >= '一' and <= '鿿'))
            return room.Contains("zh") ? "zh" : "ja";
        if (text.Any(c => c is >= 'Ѐ' and <= 'ӿ'))
            return text.Any(c => "іїєґІЇЄҐ".Contains(c)) || !room.Contains("ru") ? "uk" : "ru";
        return null;
    }
}
