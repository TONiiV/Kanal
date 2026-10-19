using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Kanal.AsrBenchmark;

// ponytail: cardinals, decimals and percent only. Ordinals, dates, years read as pairs
// ("twenty twenty-six") and digit-by-digit codes stay as words. A run like 十分 and a mix like 5万2千 stay wrong. The idioms 万一, 万万 and 千万 before 不, 别, 要 or 得 stay text.
// Upgrade path: a real ITN (NeMo, WeTextProcessing).
internal static partial class Numbers
{
    public static string Normalize(string lang, string text)
    {
        // The separator goes first: FormKC turns every no-break space into a plain space, which is not a separator.
        return lang == "zh"
            ? Chinese(text.Normalize(NormalizationForm.FormKC))
            : Latin(lang, Thousands[lang].Replace(text, "").Normalize(NormalizationForm.FormKC));
    }

    private enum Kind { Value, Hundred, Scale, Decimal, One }

    private static readonly Dictionary<string, Dictionary<string, (Kind Kind, long Value)>> Words = new()
    {
        ["en"] = Build(
            ("zero", 0), ("one", 1), ("two", 2), ("three", 3), ("four", 4), ("five", 5), ("six", 6), ("seven", 7),
            ("eight", 8), ("nine", 9), ("ten", 10), ("eleven", 11), ("twelve", 12), ("thirteen", 13), ("fourteen", 14),
            ("fifteen", 15), ("sixteen", 16), ("seventeen", 17), ("eighteen", 18), ("nineteen", 19), ("twenty", 20),
            ("thirty", 30), ("forty", 40), ("fifty", 50), ("sixty", 60), ("seventy", 70), ("eighty", 80), ("ninety", 90))
            .With(Kind.Hundred, 100, "hundred")
            .With(Kind.Scale, 1000, "thousand").With(Kind.Scale, 1_000_000, "million").With(Kind.Scale, 1_000_000_000, "billion")
            .With(Kind.Decimal, 0, "point").With(Kind.One, 1, "a"),
        ["de"] = new Dictionary<string, (Kind, long)>()
            .With(Kind.Hundred, 100, "hundert")
            .With(Kind.Scale, 1000, "tausend").With(Kind.Scale, 1_000_000, "million", "millionen")
            .With(Kind.Scale, 1_000_000_000, "milliarde", "milliarden")
            .With(Kind.Decimal, 0, "komma").With(Kind.One, 1, "ein", "eine", "einen", "einem", "einer"),
        ["pl"] = Build(
            ("zero", 0), ("jeden", 1), ("jedna", 1), ("jedno", 1), ("dwa", 2), ("dwie", 2), ("dwóch", 2), ("dwu", 2),
            ("trzy", 3), ("trzech", 3), ("dwoma", 2), ("trzema", 3), ("czterema", 4), ("pięcioma", 5), ("sześcioma", 6),
            ("siedmioma", 7), ("ośmioma", 8), ("dziewięcioma", 9), ("dziesięcioma", 10), ("cztery", 4), ("czterech", 4), ("pięć", 5), ("pięciu", 5), ("sześć", 6),
            ("sześciu", 6), ("siedem", 7), ("siedmiu", 7), ("osiem", 8), ("ośmiu", 8), ("dziewięć", 9), ("dziewięciu", 9),
            ("dziesięć", 10), ("dziesięciu", 10), ("jedenaście", 11), ("jedenastu", 11), ("dwanaście", 12), ("dwunastu", 12),
            ("trzynaście", 13), ("trzynastu", 13), ("czternaście", 14), ("czternastu", 14), ("piętnaście", 15),
            ("piętnastu", 15), ("szesnaście", 16), ("szesnastu", 16), ("siedemnaście", 17), ("siedemnastu", 17),
            ("osiemnaście", 18), ("osiemnastu", 18), ("dziewiętnaście", 19), ("dziewiętnastu", 19),
            ("dwadzieścia", 20), ("dwudziestu", 20), ("trzydzieści", 30), ("trzydziestu", 30), ("czterdzieści", 40),
            ("czterdziestu", 40), ("pięćdziesiąt", 50), ("pięćdziesięciu", 50), ("sześćdziesiąt", 60),
            ("sześćdziesięciu", 60), ("siedemdziesiąt", 70), ("siedemdziesięciu", 70), ("osiemdziesiąt", 80),
            ("osiemdziesięciu", 80), ("dziewięćdziesiąt", 90), ("dziewięćdziesięciu", 90),
            ("sto", 100), ("stu", 100), ("dwieście", 200), ("dwustu", 200), ("trzysta", 300), ("trzystu", 300),
            ("czterysta", 400), ("czterystu", 400), ("pięćset", 500), ("pięciuset", 500), ("sześćset", 600),
            ("sześciuset", 600), ("siedemset", 700), ("siedmiuset", 700), ("osiemset", 800), ("ośmiuset", 800),
            ("dziewięćset", 900), ("dziewięciuset", 900))
            .With(Kind.Scale, 1000, "tysiąc", "tysiące", "tysięcy", "tysiąca", "tysiącu", "tysiącom", "tysiącami", "tysiącach")
            .With(Kind.Scale, 1_000_000, "milion", "miliony", "milionów", "miliona", "milionie", "milionom", "milionami", "milionach")
            .With(Kind.Scale, 1_000_000_000, "miliard", "miliardy", "miliardów", "miliarda", "miliardzie", "miliardom", "miliardami", "miliardach")
            .With(Kind.One, 1, "jednego", "jednym", "jednej", "jednemu")
            .With(Kind.Decimal, 0, "przecinek"),
    };

    private static readonly HashSet<string> PolishPercent =
        ["procent", "procenty", "procentów", "procentu", "procentach", "procentami", "procentom", "procentowi"];

    private static readonly Dictionary<string, Regex> Thousands = new()
    {
        ["en"] = Separator(","),
        ["de"] = Separator(@"\."),
        ["pl"] = Separator(@"\."),
    };

    private static Regex Separator(string mark) =>
        new($@"(?<=\d)[{mark}\u00A0\u202F](?=\d{{3}}(?!\d))", RegexOptions.CultureInvariant);

    private static readonly Dictionary<string, string> Percent = new() { ["en"] = "percent", ["de"] = "prozent", ["pl"] = "procent" };

    private static string Latin(string lang, string text)
    {
        if (Percent.TryGetValue(lang, out var percent))
            text = text.Replace("%", " " + percent);
        var tokens = Scoring.Words(text);
        var output = new List<string>();
        var number = new Builder();
        for (var i = 0; i < tokens.Length; i++)
        {
            var token = lang == "pl" && PolishPercent.Contains(tokens[i]) ? "procent" : tokens[i];
            var next = i + 1 < tokens.Length ? Parse(lang, tokens[i + 1]) : null;
            var parsed = Parse(lang, token);

            if (parsed?.Kind == Kind.One && next?.Kind is not (Kind.Hundred or Kind.Scale))
                parsed = null;
            if (lang == "en" && token == "and" && next is { Kind: Kind.Value } n && !tokens[i + 1].All(char.IsAsciiDigit) && number.Accepts(n.Value))
                continue;
            if (parsed?.Kind == Kind.Decimal && number.Active && number.Fraction is null)
            {
                var digits = new StringBuilder();
                while (i + 1 < tokens.Length && Parse(lang, tokens[i + 1]) is { Kind: Kind.Value, Value: < 10 } d)
                {
                    digits.Append(d.Value);
                    i++;
                }

                if (digits.Length > 0)
                {
                    number.Fraction = digits.ToString();
                    continue;
                }

                parsed = null;
            }

            if (parsed is null || parsed.Value.Kind == Kind.Decimal)
            {
                number.Flush(output);
                output.Add(token);
                continue;
            }

            if (!number.Add(parsed.Value.Kind, parsed.Value.Value, token.All(char.IsAsciiDigit)))
            {
                number.Flush(output);
                number.Add(parsed.Value.Kind, parsed.Value.Value, token.All(char.IsAsciiDigit));
            }
        }

        number.Flush(output);
        return string.Join(' ', output);
    }

    private static (Kind Kind, long Value)? Parse(string lang, string token)
    {
        if (token.All(char.IsAsciiDigit) && long.TryParse(token, out var digits))
            return (Kind.Value, digits);
        if (Words[lang].TryGetValue(token, out var word))
            return word;
        return lang == "de" && German(token) is { } value ? (Kind.Value, value) : null;
    }

    private sealed class Builder
    {
        private long _total;
        private long _limit;
        private long _scale;
        private long _group;
        public bool Active { get; private set; }
        public string? Fraction { get; set; }

        public bool Accepts(long value) => Active && Fraction is null && value < _limit;

        public bool Add(Kind kind, long value, bool digits)
        {
            if (Fraction is not null)
                return false;
            switch (kind)
            {
                case Kind.One:
                case Kind.Value:
                    if (Active && (digits || value >= _limit))
                        return false;
                    _group += value;
                    _limit = digits ? 1 : Limit(value);
                    break;
                case Kind.Hundred:
                    if (Active && _group >= 100)
                        return false;
                    _group = Math.Max(_group, 1) * 100;
                    _limit = 100;
                    break;
                case Kind.Scale:
                    if (Active && (_scale > 0 && value >= _scale || _group >= value))
                        return false;
                    _total += Math.Max(_group, 1) * value;
                    _group = 0;
                    _scale = value;
                    _limit = value;
                    break;
            }

            Active = true;
            return true;
        }

        public void Flush(List<string> output)
        {
            if (Active)
                output.Add((_total + _group).ToString(CultureInfo.InvariantCulture) + (Fraction is null ? "" : "." + Fraction));
            _total = _group = _limit = _scale = 0;
            Active = false;
            Fraction = null;
        }

        private static long Limit(long value)
        {
            if (value is >= 10 and < 20 || value % 10 != 0 || value == 0)
                return 1;
            long limit = 1;
            while (value % (limit * 10) == 0)
                limit *= 10;
            return limit;
        }
    }

    private static readonly Dictionary<string, long> GermanSmall = new()
    {
        ["null"] = 0, ["eins"] = 1, ["zwei"] = 2, ["zwo"] = 2, ["drei"] = 3, ["vier"] = 4, ["fünf"] = 5, ["sechs"] = 6,
        ["sieben"] = 7, ["acht"] = 8, ["neun"] = 9, ["zehn"] = 10, ["elf"] = 11, ["zwölf"] = 12, ["dreizehn"] = 13,
        ["vierzehn"] = 14, ["fünfzehn"] = 15, ["sechzehn"] = 16, ["siebzehn"] = 17, ["achtzehn"] = 18, ["neunzehn"] = 19,
    };

    private static readonly Dictionary<string, long> GermanTens = new()
    {
        ["zwanzig"] = 20, ["dreißig"] = 30, ["dreissig"] = 30, ["vierzig"] = 40, ["fünfzig"] = 50, ["sechzig"] = 60,
        ["siebzig"] = 70, ["achtzig"] = 80, ["neunzig"] = 90,
    };

    private static long? German(string word)
    {
        foreach (var (unit, value) in new[] { ("tausend", 1000L), ("hundert", 100L) })
        {
            var at = word.IndexOf(unit, StringComparison.Ordinal);
            if (at < 0 || word.Length == unit.Length)
                continue;
            var left = word[..at];
            var right = word[(at + unit.Length)..];
            long? factor = left is "" or "ein" ? 1 : left == "hundert" ? 100 : German(left);
            long? rest = right == "" ? 0 : German(right);
            return factor is { } f && f < (value == 1000 ? 1000 : 10) && rest is { } r && r < value ? f * value + r : null;
        }

        if (GermanSmall.TryGetValue(word, out var small))
            return small;
        if (GermanTens.TryGetValue(word, out var tens))
            return tens;
        var und = word.IndexOf("und", StringComparison.Ordinal);
        if (und > 0 && GermanTens.TryGetValue(word[(und + 3)..], out var t))
        {
            var u = word[..und];
            if (u == "ein")
                return t + 1;
            if (GermanSmall.TryGetValue(u, out var v) && v is > 1 and < 10)
                return t + v;
        }

        return null;
    }

    private static string Chinese(string text) =>
        ChineseNumber().Replace(
            ArabicUnit().Replace(text, m =>
                ((long)Math.Round(double.Parse(m.Groups[1].Value.Replace(",", ""), CultureInfo.InvariantCulture)
                                  * m.Groups[2].Value.Aggregate(1.0, (product, unit) => product * ChineseUnit(unit))))
                .ToString(CultureInfo.InvariantCulture)),
            m => m.Groups[1].Success
                ? m.Value
                : ChineseValue(m.Groups[3].Value) + (m.Groups[2].Success ? "%" : ""));

    private static string ChineseValue(string run)
    {
        var parts = run.Split('点');
        var whole = parts[0];
        string integer;
        if (!whole.Any(c => ChineseUnit(c) > 0))
        {
            integer = string.Concat(whole.Select(c => ChineseDigit(c)));
        }
        else
        {
            long total = 0, section = 0, digit = 0, lastUnit = 0;
            var afterZero = false;
            foreach (var c in whole)
            {
                var unit = ChineseUnit(c);
                if (unit == 0)
                {
                    digit = ChineseDigit(c);
                    afterZero |= c is '零' or '〇';
                    continue;
                }

                if (unit < 10_000)
                    section += (digit == 0 && (unit == 10 || section + total == 0) ? 1 : digit) * unit;
                else if (unit == 10_000)
                    total += (section + digit == 0 ? 1 : section + digit) * unit;
                else
                    total = (total + section + digit == 0 ? 1 : total + section + digit) * unit;
                if (unit >= 10_000)
                    section = 0;
                digit = 0;
                lastUnit = unit;
                afterZero = false;
            }

            if (digit > 0 && lastUnit >= 100 && !afterZero && ChineseUnit(whole[^2]) == lastUnit)
                digit *= lastUnit / 10;
            integer = (total + section + digit).ToString(CultureInfo.InvariantCulture);
        }

        return parts.Length > 1 && parts[1].Length > 0
            ? integer + "." + string.Concat(parts[1].Select(c => ChineseDigit(c)))
            : integer;
    }

    private static long ChineseDigit(char c) => "零一二三四五六七八九".IndexOf(c) is >= 0 and var i ? i : c switch
    {
        '〇' => 0,
        '两' => 2,
        _ => 0,
    };

    private static long ChineseUnit(char c) => c switch
    {
        '十' => 10,
        '百' => 100,
        '千' => 1000,
        '万' => 10_000,
        '亿' => 100_000_000,
        _ => 0,
    };

    private static Dictionary<string, (Kind, long)> Build(params (string Word, long Value)[] words) =>
        words.ToDictionary(w => w.Word, w => (Kind.Value, w.Value));

    private static Dictionary<string, (Kind, long)> With(this Dictionary<string, (Kind, long)> words, Kind kind, long value, params string[] forms)
    {
        foreach (var form in forms)
            words[form] = (kind, value);
        return words;
    }

    [GeneratedRegex(@"(?<!\d|\d[,.])((?:\d{1,3}(?:,\d{3})+|\d+)(?:\.\d+)?)([百千万亿]+)(?![零〇一二两三四五六七八九十百千万亿])")]
    private static partial Regex ArabicUnit();

    [GeneratedRegex("(万一|万万|千万(?=[不别要得]))|(百分之)?([零〇一二两三四五六七八九十百千万亿]+(?:点[零〇一二两三四五六七八九]+)?)")]
    private static partial Regex ChineseNumber();
}
