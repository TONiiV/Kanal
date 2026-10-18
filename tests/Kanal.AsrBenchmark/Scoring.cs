using System.Text;
using System.Text.RegularExpressions;
using Kanal.Core.Text;

namespace Kanal.AsrBenchmark;

internal static partial class Scoring
{
    public static string NormalizeChinese(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (var c in SimplifiedChinese.Normalize(Compat(text)).ToLowerInvariant())
            if (char.IsLetterOrDigit(c))
                sb.Append(c);
        return sb.ToString();
    }

    public static string[] Words(string text) =>
        NotWordChar().Split(Compat(text).ToLowerInvariant()).Where(w => w.Length > 0).ToArray();

    public static (int Errors, int ReferenceLength) Errors(string lang, string hypothesis, string reference, bool numbers = false)
    {
        if (numbers)
            (hypothesis, reference) = (Numbers.Normalize(lang, hypothesis), Numbers.Normalize(lang, reference));
        if (lang == "zh")
        {
            var r = NormalizeChinese(reference);
            return (Distance(NormalizeChinese(hypothesis).ToCharArray(), r.ToCharArray()), r.Length);
        }

        var words = Words(reference);
        return (Distance(Words(hypothesis), words), words.Length);
    }

    public static string[] MixedTokens(string text)
    {
        var tokens = new List<string>();
        var word = new StringBuilder();
        void Flush()
        {
            if (word.Length > 0)
                tokens.Add(word.ToString());
            word.Clear();
        }

        foreach (var c in SimplifiedChinese.Normalize(Compat(text)).ToLowerInvariant())
        {
            if (c is >= '一' and <= '鿿' or >= '㐀' and <= '䶿')
            {
                Flush();
                tokens.Add(c.ToString());
            }
            else if (char.IsLetterOrDigit(c))
            {
                word.Append(c);
            }
            else
            {
                Flush();
            }
        }

        Flush();
        return [.. tokens];
    }

    public static (int Errors, int ReferenceLength) MixedErrors(string hypothesis, string reference, bool numbers = false)
    {
        if (numbers)
            (hypothesis, reference) = (MixedNumbers(hypothesis), MixedNumbers(reference));
        var tokens = MixedTokens(reference);
        return (Distance(MixedTokens(hypothesis), tokens), tokens.Length);
    }

    public static (int Kept, int Total) EnglishKept(string hypothesis, string reference)
    {
        hypothesis = Contraction().Replace(Compat(hypothesis), "");
        reference = Contraction().Replace(Compat(reference), "");
        var available = MixedTokens(hypothesis).Where(IsEnglishWord).GroupBy(w => w).ToDictionary(g => g.Key, g => g.Count());
        var words = EnglishWords(reference);
        var kept = 0;
        foreach (var word in words)
            if (available.TryGetValue(word, out var left) && left > 0)
            {
                available[word] = left - 1;
                kept++;
            }

        return (kept, words.Count);
    }

    private static readonly HashSet<string> Fillers = ["um", "umm", "uh", "uhh", "er", "erm", "mm", "mmm", "hm", "hmm"];

    private static bool IsEnglishWord(string token) => token.Any(char.IsAsciiLetter);

    private static bool IsSingleLetter(string token) => token.Length == 1 && char.IsAsciiLetter(token[0]);

    // A run of single letters is a spelled word ("i s m"): the reference has no single Latin word for it. A lone "a" or "I" stays.
    private static List<string> EnglishWords(string reference)
    {
        var tokens = MixedTokens(reference);
        return tokens
            .Where((t, i) => IsEnglishWord(t) && !Fillers.Contains(t)
                             && !(IsSingleLetter(t) && (i > 0 && IsSingleLetter(tokens[i - 1]) || i + 1 < tokens.Length && IsSingleLetter(tokens[i + 1]))))
            .ToList();
    }

    private static string MixedNumbers(string text) =>
        Numbers.Normalize("en", Numbers.Normalize("zh", ScriptEdge().Replace(SimplifiedChinese.Normalize(Compat(text)), " ")));

    public static IReadOnlyList<string> Codes(string source) =>
        CodeToken().Matches(Compat(source))
            .Select(m => m.Value)
            .Where(v => v.Any(char.IsDigit) && v.Any(char.IsAsciiLetterUpper))
            .ToList();

    public static int CodesKept(string source, string output, bool loose)
    {
        output = Compat(output);
        if (!loose)
            return Codes(source).Count(output.Contains);
        return Codes(source).Count(c => Regex.IsMatch(output, LoosePattern(c), RegexOptions.IgnoreCase));
    }

    // sherpa-onnx 1.13.8 keeps Qwen3's "language X<asr_text>" header in the auto-mode text of the 1.7B community export.
    public static string StripAsrHeader(string text)
    {
        var header = text.IndexOf(AsrTextTag, StringComparison.Ordinal);
        return (header < 0 ? text : text[(header + AsrTextTag.Length)..]).Trim();
    }

    private const string AsrTextTag = "<asr_text>";

    public static bool HasDigit(string text) => Compat(text).Any(char.IsDigit);

    public static double Percentile(IReadOnlyList<double> values, double p)
    {
        if (values.Count == 0)
            return 0;
        var sorted = values.Order().ToArray();
        return sorted[Math.Max(0, (int)Math.Ceiling(p * sorted.Length) - 1)];
    }

    public static int SpeechOnset(short[] samples, int frame)
    {
        var loud = Loud(samples, frame);
        return loud.Count == 0 ? 0 : loud.IndexOf(true) * frame;
    }

    public static int SpeechEnd(short[] samples, int frame)
    {
        var loud = Loud(samples, frame);
        return loud.Count == 0 ? samples.Length : (loud.LastIndexOf(true) + 1) * frame;
    }

    private static List<bool> Loud(short[] samples, int frame)
    {
        var rms = new List<double>();
        for (var i = 0; i + frame <= samples.Length; i += frame)
        {
            double sum = 0;
            for (var j = i; j < i + frame; j++)
                sum += (double)samples[j] * samples[j];
            rms.Add(Math.Sqrt(sum / frame));
        }

        var threshold = rms.DefaultIfEmpty().Max() / 10;
        return rms.Select(r => r >= threshold).ToList();
    }

    private static int Distance<T>(IReadOnlyList<T> a, IReadOnlyList<T> b)
    {
        var previous = Enumerable.Range(0, b.Count + 1).ToArray();
        var current = new int[b.Count + 1];
        for (var i = 1; i <= a.Count; i++)
        {
            current[0] = i;
            for (var j = 1; j <= b.Count; j++)
            {
                var substitution = previous[j - 1] + (EqualityComparer<T>.Default.Equals(a[i - 1], b[j - 1]) ? 0 : 1);
                current[j] = Math.Min(substitution, Math.Min(previous[j], current[j - 1]) + 1);
            }

            (previous, current) = (current, previous);
        }

        return previous[b.Count];
    }

    private static string Compat(string text) => text.Normalize(NormalizationForm.FormKC);

    private static string LoosePattern(string code) =>
        "(?<![A-Za-z0-9])" + string.Join(@"[\s\-./]*", code.Where(char.IsAsciiLetterOrDigit).Select(c => c.ToString())) + "(?![A-Za-z0-9])";

    [GeneratedRegex(@"(?<=[\u4e00-\u9fff\u3400-\u4dbf])(?=[A-Za-z])|(?<=[A-Za-z])(?=[\u4e00-\u9fff\u3400-\u4dbf])")]
    private static partial Regex ScriptEdge();

    // A contraction stays one word: split at the apostrophe, "I'm" would look like a spelled word.
    [GeneratedRegex(@"(?<=[A-Za-z])['\u2019](?=[A-Za-z])")]
    private static partial Regex Contraction();

    [GeneratedRegex(@"[^\p{L}\p{N}]+")]
    private static partial Regex NotWordChar();

    [GeneratedRegex(@"(?<![A-Za-z0-9])[A-Z0-9][A-Za-z0-9]*(?:-[A-Za-z0-9]+)*(?![A-Za-z0-9])")]
    private static partial Regex CodeToken();
}
